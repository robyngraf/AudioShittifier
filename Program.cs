using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Lame;
using NAudio.Wave;

string inputPath = @"C:\Users\Robyn\Downloads\kevin-macleod-hall-of-the-mountain-king.mp3";
string outputPath = Path.ChangeExtension(inputPath, "_shitty.mp3");
int frequencyLimit = 8; // Number of frequencies to keep per frame
int fftSize = 576; // 576; // 2048;

int hopSize = fftSize / 2;

using var reader = new AudioFileReader(inputPath);
int sampleRate = reader.WaveFormat.SampleRate;
int channels = reader.WaveFormat.Channels;

// Read entire file into float array
var samples = new float[reader.Length / sizeof(float)];
reader.Read(samples, 0, samples.Length);

// Process each channel independently
float[][] Deinterleave(float[] samples, int channels)
{
    int length = samples.Length / channels;
    float[][] result = new float[channels][];

    for (int ch = 0; ch < channels; ch++)
    {
        result[ch] = new float[length];
        for (int i = 0; i < length; i++)
            result[ch][i] = samples[i * channels + ch];
    }

    return result;
}
float[][] channelData = Deinterleave(samples, channels);

float[] ProcessChannel(float[] input, int fftSize, int hopSize)
{
    var window = Window.Hann(fftSize);
    float[] output = new float[input.Length + fftSize];

    for (int pos = 0; pos + fftSize < input.Length; pos += hopSize)
    {
        // Windowed frame
        Complex32[] frame = new Complex32[fftSize];
        for (int i = 0; i < fftSize; i++)
            frame[i] = new Complex32(input[pos + i] * (float)window[i], 0f);

        // FFT
        Fourier.Forward(frame, FourierOptions.Matlab);

        // Select just a few of the loudest frequencies & discard phase
        var tops = frame.Select((Sample, I) => new { Sample, I }).OrderByDescending(x => x.Sample.Magnitude).Take(frequencyLimit).ToList();
        frame = new Complex32[fftSize];
        foreach (var item in tops)
        {
            frame[item.I] = new Complex32(item.Sample.Magnitude, 0f);
        }

        // Inverse FFT
        Fourier.Inverse(frame, FourierOptions.Matlab);

        // Overlap-add
        for (int i = 0; i < fftSize; i++)
            output[pos + i] += frame[i].Real * (float)window[i];
    }

    return output;
}

for (int ch = 0; ch < channels; ch++)
{
    channelData[ch] = ProcessChannel(channelData[ch], fftSize, hopSize);
}

// Normalize to original energy level
float processedRMS = (float)Math.Sqrt(channelData.SelectMany(c => c).Average(s => s * s));
float originalRMS = (float)Math.Sqrt(samples.Average(s => s * s));
var volumeAdjustment = originalRMS / processedRMS;
for (int ch = 0; ch < channels; ch++)
{
    for (int i = 0; i < channelData[ch].Length; i++)
    {
        channelData[ch][i] *= volumeAdjustment;
    }
}

// Re-interleave 
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

/*
// Write output
void WriteWav(string path, float[] samples, int sampleRate, int channels)
{
    var fmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    using var writer = new WaveFileWriter(path, fmt);
    writer.WriteSamples(samples, 0, samples.Length);
}
WriteWav(outputPath, output, sampleRate, channels);
*/

// Write mp3 using NAudio.Lame
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
    var mp3Writer = new LameMP3FileWriter(path, reader.WaveFormat, LAMEPreset.STANDARD);
    reader.CopyTo(mp3Writer);
}