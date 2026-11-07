using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland.Services;

/// <summary>
/// "Mendengarkan" suara yang keluar dari speaker (WASAPI loopback), lalu memecahnya
/// menjadi beberapa pita frekuensi untuk bar musik. Hanya berjalan saat musik diputar.
/// </summary>
public sealed class AudioVisualizer : IDisposable
{
    const int FftSize = 1024;   // 2^10
    const int FftExponent = 10;

    readonly object _lock = new();
    readonly Complex[] _fft = new Complex[FftSize];
    readonly float[] _window = new float[FftSize];
    WasapiLoopbackCapture? _capture;
    float[] _targets;
    int _fftPos;
    int _channels = 2;
    int _sampleRate = 48000;
    bool _isFloat = true;
    long _lastDataTick;

    public AudioVisualizer(int bars)
    {
        _targets = new float[bars];
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
    }

    public bool IsRunning => _capture != null;

    public void SetBarCount(int bars)
    {
        lock (_lock) _targets = new float[bars];
    }

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            var cap = new WasapiLoopbackCapture();
            _channels = Math.Max(1, cap.WaveFormat.Channels);
            _sampleRate = cap.WaveFormat.SampleRate;
            _isFloat = cap.WaveFormat.BitsPerSample == 32; // loopback hampir selalu float 32-bit
            cap.DataAvailable += OnData;
            cap.StartRecording();
            _capture = cap;
        }
        catch
        {
            _capture = null;
        }
    }

    public void Stop()
    {
        var cap = _capture;
        _capture = null;
        if (cap == null) return;
        try
        {
            cap.DataAvailable -= OnData;
            cap.StopRecording();
            cap.Dispose();
        }
        catch { }
        lock (_lock) Array.Clear(_targets);
    }

    /// <summary>Salin level terbaru (0..1) ke array tujuan.</summary>
    public void ReadLevels(float[] dest)
    {
        bool stale = Environment.TickCount64 - _lastDataTick > 200; // tidak ada suara masuk
        lock (_lock)
        {
            int n = Math.Min(dest.Length, _targets.Length);
            for (int i = 0; i < n; i++) dest[i] = stale ? 0 : _targets[i];
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (!_isFloat) return;
        _lastDataTick = Environment.TickCount64;

        int frameBytes = 4 * _channels;
        int frames = e.BytesRecorded / frameBytes;
        var buf = e.Buffer;

        for (int f = 0; f < frames; f++)
        {
            int offset = f * frameBytes;
            float sum = 0;
            for (int ch = 0; ch < _channels; ch++)
                sum += BitConverter.ToSingle(buf, offset + ch * 4);

            _fft[_fftPos].X = sum / _channels * _window[_fftPos];
            _fft[_fftPos].Y = 0;
            _fftPos++;

            if (_fftPos >= FftSize)
            {
                _fftPos = 0;
                ComputeBands();
            }
        }
    }

    void ComputeBands()
    {
        FastFourierTransform.FFT(true, FftExponent, _fft);

        float[] targets;
        lock (_lock) targets = _targets;
        int bars = targets.Length;
        double fMin = 60, fMax = Math.Min(12000, _sampleRate / 2.0);

        for (int b = 0; b < bars; b++)
        {
            // pita frekuensi logaritmik: bass di kiri, treble di kanan
            double lo = fMin * Math.Pow(fMax / fMin, (double)b / bars);
            double hi = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / bars);
            int i0 = Math.Max(1, (int)(lo * FftSize / _sampleRate));
            int i1 = Math.Max(i0 + 1, (int)(hi * FftSize / _sampleRate));

            double peak = 0;
            for (int i = i0; i < i1 && i < FftSize / 2; i++)
            {
                double mag = Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                if (mag > peak) peak = mag;
            }

            double db = 20 * Math.Log10(peak + 1e-9);
            float v = (float)Math.Clamp((db + 72) / 54.0, 0, 1);
            lock (_lock)
            {
                if (b < _targets.Length) _targets[b] = v;
            }
        }
    }

    public void Dispose() => Stop();
}
