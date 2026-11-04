// AudioBoostWavCodec.cs   (editor only - pure C#, no UnityEngine references)
// --------------------------------------------------------------------------
// WAV reader/writer used by Audio Boost Studio. Lives in an Editor folder.
//
// Design goals:
//   1. Bit-exact round trip: Load -> Save without applying gain reproduces
//      the file byte for byte, because every chunk is copied verbatim and
//      integer PCM <-> float conversion is exact for 8/16/24-bit (and the
//      identity for 32-bit float), so re-encoded 'data' payloads match.
//   2. Supported layouts: PCM 8/16/24-bit and IEEE float 32-bit, including
//      WAVE_FORMAT_EXTENSIBLE wrappers (the SubFormat GUID decides the real
//      format). Everything else fails gracefully with Ok = false and a
//      human-readable Error - the window then suggests the AudioBoostFilter
//      component instead.
//   3. ApplyGain() scales the interleaved samples in memory and refreshes
//      Peak/Rms/PeakDb/RmsDb, so callers can show before -> after numbers.
using System;
using System.Collections.Generic;
using System.IO;

public sealed class WavClip
{
    // ---------------- public state ----------------
    public bool Ok;
    public string Error = "";
    public int Channels;
    public int SampleRate;
    public int BitsPerSample;
    public bool IsFloat;
    public int SampleCount;      // interleaved sample count (frames * channels)
    public float[] Samples;      // interleaved, linear -1..1
    public float Peak;           // linear
    public float PeakDb;         // dBFS
    public float Rms;            // linear
    public float RmsDb;          // dBFS

    // ---------------- internal state ----------------
    // Every chunk of the source file, verbatim (8-byte header included),
    // in original order. Only the 'data' chunk is re-encoded on Save.
    private readonly List<byte[]> chunks = new List<byte[]>();
    private int fmtIndex = -1;
    private int dataIndex = -1;

    public static float ToDb(float v)
    {
        return (float)(20.0 * Math.Log10(Math.Max((double)v, 1e-12)));
    }

    public static float ToLinear(float db)
    {
        return (float)Math.Pow(10.0, (double)db / 20.0);
    }

    public static WavClip Load(string path)
    {
        WavClip w = new WavClip();
        try
        {
            w.Parse(File.ReadAllBytes(path));
        }
        catch (Exception e)
        {
            w.Ok = false;
            w.Error = e.Message;
        }
        return w;
    }

    // Multiplies every sample by 'gain' and refreshes the analysis values.
    public void ApplyGain(float gain)
    {
        if (!Ok || Samples == null)
        {
            return;
        }
        float[] s = Samples;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = s[i] * gain;
        }
        Analyze();
    }

    public void Save(string path)
    {
        if (!Ok || Samples == null)
        {
            throw new InvalidOperationException("WavClip is not usable: " + Error);
        }
        byte[] payload = EncodeSamples();
        using (MemoryStream ms = new MemoryStream())
        {
            ms.WriteByte((byte)'R'); ms.WriteByte((byte)'I'); ms.WriteByte((byte)'F'); ms.WriteByte((byte)'F');
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // RIFF size placeholder
            ms.WriteByte((byte)'W'); ms.WriteByte((byte)'A'); ms.WriteByte((byte)'V'); ms.WriteByte((byte)'E');
            for (int i = 0; i < chunks.Count; i++)
            {
                if (i == dataIndex)
                {
                    ms.WriteByte((byte)'d'); ms.WriteByte((byte)'a'); ms.WriteByte((byte)'t'); ms.WriteByte((byte)'a');
                    ms.Write(BitConverter.GetBytes((uint)payload.Length), 0, 4);
                    ms.Write(payload, 0, payload.Length);
                    if ((payload.Length & 1) == 1)
                    {
                        ms.WriteByte(0); // chunks are padded to even sizes
                    }
                }
                else
                {
                    byte[] c = chunks[i];
                    ms.Write(c, 0, c.Length);
                }
            }
            byte[] all = ms.ToArray();
            Buffer.BlockCopy(BitConverter.GetBytes((uint)(all.Length - 8)), 0, all, 4, 4);
            File.WriteAllBytes(path, all);
        }
    }

    // ---------------- internals ----------------

    private void Parse(byte[] file)
    {
        if (file == null || file.Length < 12)
        {
            Fail("file too small to be a WAV");
            return;
        }
        if (ReadFourCC(file, 0) != "RIFF" || ReadFourCC(file, 8) != "WAVE")
        {
            Fail("not a RIFF/WAVE file");
            return;
        }

        int pos = 12;
        while (pos + 8 <= file.Length)
        {
            string id = ReadFourCC(file, pos);
            uint size = BitConverter.ToUInt32(file, pos + 4);
            long chunkEnd = (long)pos + 8 + (long)size;
            if (chunkEnd > file.Length)
            {
                Fail("truncated chunk '" + id + "'");
                return;
            }
            int stored = (int)(8 + size);
            if ((size & 1) == 1 && chunkEnd < file.Length)
            {
                stored++; // trailing pad byte, only if the file actually has it
            }
            byte[] raw = new byte[stored];
            Buffer.BlockCopy(file, pos, raw, 0, stored);
            chunks.Add(raw);
            if (id == "fmt ")
            {
                fmtIndex = chunks.Count - 1;
            }
            else if (id == "data")
            {
                dataIndex = chunks.Count - 1;
            }
            pos += stored;
        }

        if (fmtIndex < 0)
        {
            Fail("no fmt chunk found");
            return;
        }
        if (dataIndex < 0)
        {
            Fail("no data chunk found");
            return;
        }

        byte[] fmt = chunks[fmtIndex];
        uint fmtSize = BitConverter.ToUInt32(fmt, 4);
        if (fmtSize < 16 || fmt.Length < 8 + 16)
        {
            Fail("fmt chunk too small");
            return;
        }

        int tag = fmt[8] | (fmt[9] << 8);
        Channels = fmt[10] | (fmt[11] << 8);
        SampleRate = BitConverter.ToInt32(fmt, 12);
        BitsPerSample = fmt[22] | (fmt[23] << 8);

        if (tag == 0xFFFE && fmtSize >= 40 && fmt.Length >= 8 + 40)
        {
            // WAVE_FORMAT_EXTENSIBLE: the first two bytes of the SubFormat
            // GUID hold the real format tag (1 = PCM, 3 = IEEE float).
            int sub = fmt[8 + 24] | (fmt[8 + 25] << 8);
            if (sub == 1 || sub == 3)
            {
                tag = sub;
            }
        }

        IsFloat = tag == 3;
        bool supported = (tag == 3 && BitsPerSample == 32)
            || (tag == 1 && (BitsPerSample == 8 || BitsPerSample == 16 || BitsPerSample == 24));
        if (!supported)
        {
            Fail("unsupported layout (" + BitsPerSample + "-bit, tag " + tag + ") - use the AudioBoostFilter component instead");
            return;
        }
        if (Channels < 1 || SampleRate < 1)
        {
            Fail("invalid fmt values");
            return;
        }

        // Decode the 'data' payload.
        byte[] data = chunks[dataIndex];
        uint dataSize = BitConverter.ToUInt32(data, 4);
        int payloadLen = (int)Math.Min((long)dataSize, (long)(data.Length - 8));
        int bytesPer = BitsPerSample / 8;
        if (payloadLen < bytesPer)
        {
            Fail("empty data chunk");
            return;
        }
        int count = payloadLen / bytesPer;
        float[] s = new float[count];
        int p = 8;
        if (IsFloat)
        {
            for (int i = 0; i < count; i++)
            {
                s[i] = BitConverter.ToSingle(data, p + i * 4);
            }
        }
        else if (BitsPerSample == 8)
        {
            for (int i = 0; i < count; i++)
            {
                s[i] = (data[p + i] - 128) / 128f;
            }
        }
        else if (BitsPerSample == 16)
        {
            for (int i = 0; i < count; i++)
            {
                s[i] = (short)(data[p + i * 2] | (data[p + i * 2 + 1] << 8)) / 32768f;
            }
        }
        else // 24-bit
        {
            for (int i = 0; i < count; i++)
            {
                int v = data[p + i * 3] | (data[p + i * 3 + 1] << 8) | (data[p + i * 3 + 2] << 16);
                if ((v & 0x800000) != 0)
                {
                    v -= 0x1000000; // sign-extend 24-bit two's complement
                }
                s[i] = v / 8388608f;
            }
        }

        Samples = s;
        SampleCount = count;
        Analyze();

        if (Peak <= 0f)
        {
            Fail("digital silence (peak 0) - nothing to boost");
            return;
        }
        Ok = true;
        Error = "";
    }

    private void Analyze()
    {
        float[] s = Samples;
        if (s == null || s.Length == 0)
        {
            Peak = 0f; Rms = 0f; PeakDb = ToDb(0f); RmsDb = ToDb(0f);
            return;
        }
        double peak = 0.0;
        double sum = 0.0;
        for (int i = 0; i < s.Length; i++)
        {
            double a = s[i] < 0f ? -(double)s[i] : s[i];
            if (a > peak)
            {
                peak = a;
            }
            sum += (double)s[i] * s[i];
        }
        Peak = (float)peak;
        Rms = (float)Math.Sqrt(sum / s.Length);
        PeakDb = ToDb(Peak);
        RmsDb = ToDb(Rms);
    }

    private byte[] EncodeSamples()
    {
        float[] s = Samples;
        int n = s.Length;
        if (IsFloat)
        {
            byte[] b = new byte[n * 4];
            for (int i = 0; i < n; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(s[i]), 0, b, i * 4, 4);
            }
            return b;
        }
        if (BitsPerSample == 8)
        {
            byte[] b = new byte[n];
            for (int i = 0; i < n; i++)
            {
                b[i] = (byte)(RoundClamp((double)s[i] * 128.0, -128, 127) + 128);
            }
            return b;
        }
        if (BitsPerSample == 16)
        {
            byte[] b = new byte[n * 2];
            for (int i = 0; i < n; i++)
            {
                int v = RoundClamp((double)s[i] * 32768.0, -32768, 32767);
                b[i * 2] = (byte)(v & 0xFF);
                b[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return b;
        }
        // 24-bit
        byte[] b24 = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            int v = RoundClamp((double)s[i] * 8388608.0, -8388608, 8388607);
            b24[i * 3] = (byte)(v & 0xFF);
            b24[i * 3 + 1] = (byte)((v >> 8) & 0xFF);
            b24[i * 3 + 2] = (byte)((v >> 16) & 0xFF);
        }
        return b24;
    }

    private static int RoundClamp(double v, int lo, int hi)
    {
        int r = (int)Math.Round(v, MidpointRounding.AwayFromZero);
        if (r < lo)
        {
            return lo;
        }
        if (r > hi)
        {
            return hi;
        }
        return r;
    }

    private static string ReadFourCC(byte[] b, int i)
    {
        return "" + (char)b[i] + (char)b[i + 1] + (char)b[i + 2] + (char)b[i + 3];
    }

    private void Fail(string msg)
    {
        Ok = false;
        Error = msg;
    }
}