using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Lame;
using NAudio.Wave;
using System.Threading.Tasks;

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

var originalLength = samples.Length / channels;
var lengthWhileProcessing = (int)Math.Ceiling((double)originalLength / fftSize) * fftSize; // Round up to multiple of fftSize

// Process each channel independently
float[][] Deinterleave(float[] samples, int channels)
{
    int length = samples.Length / channels;
    float[][] result = new float[channels][];

    for (int ch = 0; ch < channels; ch++)
    {
        result[ch] = new float[lengthWhileProcessing];
        for (int i = 0; i < length; i++)
            result[ch][i] = samples[i * channels + ch];
    }

    return result;
}
float[][] channelData = Deinterleave(samples, channels);



float[] ProcessChannel(float[] input, int fftSize, int hopSize)
{
    var window = Window.Hann(fftSize);
    int outputLength = input.Length;
    float[] output = new float[outputLength];

    if (input.Length <= fftSize)
        throw new ArgumentException("Input length must be greater than FFT size.");

    int frames = 1 + ((input.Length - fftSize - 1) / hopSize);

    object mergeLock = new();

    Parallel.For(0, frames,
        frameIndex =>
        {
            var pos = frameIndex * hopSize;

            // Windowed frame
            Complex32[] frame = new Complex32[fftSize];
            for (int i = 0; i < fftSize; i++)
                frame[i] = new Complex32(input[pos + i] * (float)window[i], 0f);

            // FFT
            Fourier.Forward(frame, FourierOptions.Matlab);

            // Select just a few of the loudest frequencies & discard phase
            var tops = frame.Select((Sample, I) => new { Sample, I }).OrderByDescending(x => x.Sample.Magnitude).Take(frequencyLimit).ToList();
            var sparse = new Complex32[fftSize];
            foreach (var item in tops)
            {
                sparse[item.I] = new Complex32(item.Sample.Magnitude, 0f);
            }

            // Inverse FFT
            Fourier.Inverse(sparse, FourierOptions.Matlab);

            // Overlap-add to merge into into final output
            lock (mergeLock)
            {
                for (int i = 0; i < fftSize; i++)
                    output[pos + i] += sparse[i].Real * (float)window[i];
            }
        });

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