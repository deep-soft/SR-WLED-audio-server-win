﻿using FftSharp;
using NAudio.Wave;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WledSRServer;

internal class Program
{
    volatile static bool keepRunning = true;
    private static ManualResetEventSlim showDisplay = new();
    volatile static int audioProcessMs = 0;
    volatile static int packetSendMs = 0;
    volatile static int packetTimingMs = 0;

    volatile static AudioSyncPacket_v2 packet = new();

    private static void Main(string[] args)
    {
        Console.WriteLine("===========================================================================");
        Console.WriteLine("                      WLED SoundReactive audio server                      ");
        Console.WriteLine("===========================================================================");

        if (!StartCapture())
            return;

        var sender = new Thread(new ThreadStart(SenderThread));
        var display = new Thread(new ThreadStart(DisplayThread));
        sender.Start();
        display.Start();

        Console.WriteLine("Press a key to exit");
        Console.ReadKey();
        keepRunning = false;

        sender.Join();
        display.Join();

        Console.WriteLine("End.");
    }

    private static bool StartCapture()
    {
        // var mmde = new MMDeviceEnumerator(); - endpoints
        var capture = new WasapiLoopbackCapture();

        var channelToCapture = 0;

        Console.WriteLine($"Capture WaveFormat: {capture.WaveFormat}");
        if (capture.WaveFormat.Channels < 1)
        {
            Console.Write($"Zero channel detected. We need at least one.");
            return false;
        }

        Func<byte[], int, double> converter;
        switch (capture.WaveFormat.BitsPerSample)
        {
            case 8:
                converter = (buffer, position) => (sbyte)buffer[position]; // - probably bad, need test case
                break;
            case 16:
                converter = (buffer, position) => BitConverter.ToInt16(buffer, position); // needs test case
                break;
            // case 24:
            //     // 3 byte => int32
            //     converter = (buffer, position) => BitConverter.ToInt32(buffer, position);
            //     byteStep = 3;
            //     break;
            case 32:
                if (capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                    converter = (buffer, position) => BitConverter.ToSingle(buffer, position);
                else
                    converter = (buffer, position) => BitConverter.ToInt32(buffer, position); // needs test case
                break;
            default:
                Console.Write("Unsupported format");
                return false;
        }

        var fftWindow = new FftSharp.Windows.Hanning();

        var outputBands = packet.fftResult.Length;

        // logarithmic freq scale
        var minFreq = 20;
        var maxFreq = capture.WaveFormat.SampleRate / 2; // fftFreq[fftFreq.Length - 1];
        var freqDiv = maxFreq / minFreq;
        var logFreqs = Enumerable.Range(0, outputBands + 1).Select(i => minFreq * Math.Pow(freqDiv, (double)i / outputBands)).ToArray();

        var sw = new Stopwatch();

        double agcMaxValue = 0;
        var buckets = new double[outputBands];
        var bucketFreq = new string[outputBands];

        capture.DataAvailable += (s, e) =>
        {
            sw.Restart();
            if (e.BytesRecorded == 0)
            {
                SetPackToZero();
                return;
            }

            // ===[ Collect samples ]================================================================================================

            int sampleCount = e.BytesRecorded / capture.WaveFormat.BlockAlign;  // All available Sample
            //sampleCount = (int)Math.Pow(2, Math.Floor(Math.Log2(sampleCount))); // Samples to FFT (must be pow of 2) - or use FftSharp.Pad.ZeroPad(windowed_samples);
            //freq count = (2^(exponent-1))+1 - 6=33, 7=65, 8=129, 9=257, 10=513, 11=1025 ...

            var values = new double[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                int position = (i + channelToCapture) * capture.WaveFormat.BlockAlign;
                values[i] = converter(e.Buffer, position);
            }

            var valMax = values.Max();
            if (valMax < 0.00001)
            {
                SetPackToZero();
                return;
            }

            // ===[ FFT ]================================================================================================

            fftWindow.ApplyInPlace(values, true);
            values = Pad.ZeroPad(values);
            double[] fftPower = FFT.Magnitude(FFT.Forward(values));
            double[] fftFreq = FFT.FrequencyScale(fftPower.Length, capture.WaveFormat.SampleRate);

            // ===[ Peaks ]================================================================================================

            double peakFreq = 0;
            double peakPower = 0;
            for (int i = 0; i < fftPower.Length; i++)
            {
                if (fftPower[i] > peakPower)
                {
                    peakPower = fftPower[i];
                    peakFreq = fftFreq[i];
                }
            }

            // ===[ AGC ]================================================================================================

            if (agcMaxValue < peakPower)
                agcMaxValue = peakPower;
            else
                agcMaxValue = (agcMaxValue * 0.9 + peakPower * 0.1);

            // ===[ Output FFT buckets ]================================================================================================

            for (var bucket = 0; bucket < buckets.Length; bucket++)
            {
                var freqRange = fftFreq.Select((freq, idx) => new { freq, idx }).Where(itm => itm.freq >= logFreqs[bucket] && itm.freq <= logFreqs[bucket + 1]).ToArray();
                var min = freqRange.First();
                var max = freqRange.Last();
                var bucketItems = fftPower.Skip(min.idx).Take(max.idx - min.idx + 1).ToArray();
                buckets[bucket] = bucketItems.Max();
                bucketFreq[bucket] = $"{min.freq:f2}hz - {max.freq:f2}hz - {bucketItems.Length} count";
            }

            // ===[ Set packet properties ]================================================================================================

            var bucketMin = buckets.Min();
            //var bucketSpan = buckets.Max() - bucketMin;
            //var bucketSpan = peakPower - bucketMin;
            var bucketSpan = agcMaxValue - bucketMin;
            for (var bucket = 0; bucket < buckets.Length; bucket++)
                packet.fftResult[bucket] = (byte)((buckets[bucket] - bucketMin) * 255 / bucketSpan);

            packet.sampleRaw = (float)peakPower;
            packet.sampleSmth = (float)peakPower;
            packet.samplePeak = 1;
            packet.FFT_Magnitude = (float)peakPower;
            packet.FFT_MajorPeak = (float)peakFreq;

            // ===[ Rinse and repeat ]================================================================================================

            audioProcessMs = (int)sw.ElapsedMilliseconds;

            if (!keepRunning)
                capture.StopRecording();
        };

        capture.RecordingStopped += (s, e) =>
        {
            keepRunning = false;
            capture.Dispose();
        };

        capture.StartRecording();
        return true;
    }

    private static void SetPackToZero()
    {
        packet.sampleRaw = 0;
        packet.sampleSmth = 0;
        packet.samplePeak = 0;
        packet.fftResult = new byte[16] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        packet.FFT_Magnitude = 0;
        packet.FFT_MajorPeak = 0;
    }

    private static void SenderThread()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("239.0.0.1"), AppConfig.WLedMulticastGroupPort);
        Console.WriteLine($"UDP endpoint: {endpoint}");
        Console.WriteLine($"Binding to address: {AppConfig.LocalIPToBind}");

        using (var client = new UdpClient(AddressFamily.InterNetwork))
        {
            client.Client.Bind(new IPEndPoint(AppConfig.LocalIPToBind, 0));

            Console.WriteLine("UDP connected, sending data");
            Console.WriteLine();
            showDisplay.Set();

            var curTop = Console.CursorTop;
            var fft = new byte[16];
            var sw = new Stopwatch();
            while (keepRunning)
            {
                sw.Restart();

                client.Send(packet.AsByteArray(), endpoint);

                packetSendMs = (int)sw.ElapsedMilliseconds;

                if (sw.ElapsedMilliseconds < 50)
                    Thread.Sleep(50 - (int)sw.ElapsedMilliseconds);

                packetTimingMs = (int)sw.ElapsedMilliseconds;
            }

            client.Close();
        }

        Console.WriteLine();
        Console.WriteLine("Sender thread stopped.");
    }

    private static void DisplayThread()
    {
        showDisplay.Wait();
        var curTop = Console.CursorTop;
        while (keepRunning)
        {
            Console.CursorVisible = false;
            Console.CursorTop = curTop;
            Console.CursorLeft = 0;
            Console.WriteLine("===[ packet preview ]======================================================");

            Console.WriteLine($"sampleRaw  : {packet.sampleRaw,-20}");
            Console.WriteLine($"sampleSmth : {packet.sampleSmth,-20}");
            Console.WriteLine($"samplePeak : {packet.samplePeak,-20}");
            for (var i = 0; i < packet.fftResult.Length; i++)
            {
                string bar = new('#', (int)(packet.fftResult[i] / 4));
                Console.WriteLine($"[{bar.PadRight(63, '-')}] {packet.fftResult[i],-3}  ");
            }
            Console.WriteLine($"FFT_Magnitude : {packet.FFT_Magnitude,-20}");
            Console.WriteLine($"FFT_MajorPeak : {packet.FFT_MajorPeak,-20}");
            Console.WriteLine();

            Console.WriteLine("===[ stats ]===============================================================");
            Console.WriteLine($"audioProcess: {audioProcessMs}ms    ");
            Console.WriteLine($"packetSend:   {packetSendMs}ms      ");
            Console.WriteLine($"packetTiming: {packetTimingMs}ms    ");
        }

        Console.CursorVisible = true;
    }
}