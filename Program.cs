using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Lame;
using NAudio.Wave;

string inputPath = @"C:\Users\Robyn\Downloads\kevin-macleod-hall-of-the-mountain-king.mp3";
int frequencyLimit = 8; // Number of frequencies to keep per frame
int fftSize = 576; // 576; // 2048;

int hopSize = fftSize / 2;
string outputPath = Path.ChangeExtension(inputPath, "_shitty.mp3");

using var reader = new AudioFileReader(inputPath);
int sampleRate = reader.WaveFormat.SampleRate;
int channels = reader.WaveFormat.Channels;

// Read entire file into float array
Console.WriteLine($"Reading audio file: {inputPath}");
var samples = new float[reader.Length / sizeof(float)];
reader.Read(samples, 0, samples.Length);

var originalLength = samples.Length / channels;
var lengthWhileProcessing = (int)Math.Ceiling((double)originalLength / fftSize) * fftSize; // Round up to multiple of fftSize

// Process each channel independently
Console.WriteLine("Deinterleaving channels...");
float[][] Deinterleave(float[] samples, int channels)
{
    int length = samples.Length / channels;
    float[][] result = new float[channels][];

    Parallel.For(0, channels, ch =>
        {
            var channel = new float[lengthWhileProcessing];
            for (int i = 0; i < length; i++)
                channel[i] = samples[i * channels + ch];
            result[ch] = channel;
        }
    );

    return result;
}
float[][] channelData = Deinterleave(samples, channels);

Console.WriteLine("Processing");
float[] ProcessChannel(float[] input, int fftSize, int hopSize)
{
    var window = Window.Hann(fftSize);
    int outputLength = input.Length;
    float[] output = new float[outputLength];

    if (input.Length <= fftSize)
        throw new ArgumentException("Input length must be greater than FFT size.");

    int frameCount = 1 + ((input.Length - fftSize - 1) / hopSize);
    int[] frameProcessingOrder = [.. Enumerable.Range(0, frameCount)];
    Random.Shared.Shuffle(frameProcessingOrder);

    object[] mergeLocks = [.. Enumerable.Range(0, frameCount).Select(i => new object())];

    void ProcessFrame(int frameIndex)
    {
        var pos = frameIndex * hopSize;

        // Windowed frame
        Complex32[] frame = new Complex32[fftSize];
        for (int i = 0; i < fftSize; i++)
            frame[i] = new Complex32(input[pos + i] * (float)window[i], 0f);

        // FFT
        Fourier.Forward(frame, FourierOptions.Matlab);

        var flattenedHarmonics = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            var x = frame[i].Magnitude;
            // bias towards notes with harmonics
            for (int j = 2; j < 16 & i * j < fftSize; j++)
                x *= frame[i * j].Magnitude + 1;

            // bias against tones that are harmonics of an alreay identified note
            for (int j = 2; j < 16 && i % j == 0 && i / j > 0; j++)
                x -= flattenedHarmonics[i / j] / 2;

            flattenedHarmonics[i] = x;
        }

        // Select just a few of the loudest frequencies & discard phase
        var tops = flattenedHarmonics.Select((Sample, I) => new { Sample, I }).OrderByDescending(x => x.Sample).Take(frequencyLimit).ToList();
        var sparse = new Complex32[fftSize];
        foreach (var item in tops)
            sparse[item.I] = frame[item.I];

        // Inverse FFT
        Fourier.Inverse(sparse, FourierOptions.Matlab);

        // Overlap-add to merge into into final output
        var l = mergeLocks[frameIndex];
        var l1 = mergeLocks[Math.Max(frameIndex - 1, 0)];
        lock (l) lock (l1)
            for (int i = 0; i < fftSize; i++)
                output[pos + i] += sparse[i].Real * (float)window[i];
    }

    Parallel.ForEach(frameProcessingOrder, ProcessFrame);
    return output;
}
Parallel.For(0, channels, ch => { channelData[ch] = ProcessChannel(channelData[ch], fftSize, hopSize); });

// Normalize to original energy level
Console.WriteLine("Normalizing");
float processedRMS = (float)Math.Sqrt(channelData.SelectMany(c => c).Average(s => s * s));
float originalRMS = (float)Math.Sqrt(samples.Average(s => s * s));
var volumeAdjustment = originalRMS / processedRMS;
Parallel.ForEach(channelData, channel => { for (int i = 0; i < channel.Length; i++) channel[i] *= volumeAdjustment; });

// Re-interleave
Console.WriteLine("Interleaving channels...");
float[] Interleave(float[][] channels, int channelCount)
{
    int length = channels[0].Length;
    float[] result = new float[length * channelCount];

    for (int i = 0; i < length; i++)
        for (int ch = 0; ch < channelCount; ch++)
            result[i * channelCount + ch] = channels[ch][i];

    return result;
}
float[] output = Interleave(channelData, channels);

// Write mp3 using NAudio.Lame
Console.WriteLine($"Writing output file: {outputPath}");
WriteMp3(outputPath, output, sampleRate, channels);
void WriteMp3(string path, float[] samples, int sampleRate, int channels)
{
    var fmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    using var ms = new MemoryStream();
    using var writer = new WaveFileWriter(ms, fmt);
    writer.WriteSamples(samples, 0, samples.Length);
    writer.Flush();
    ms.Position = 0;
    using var reader = new WaveFileReader(ms);
    var mp3Writer = new LameMP3FileWriter(path, reader.WaveFormat, LAMEPreset.V9);
    reader.CopyTo(mp3Writer);
}

Console.WriteLine("Done!");