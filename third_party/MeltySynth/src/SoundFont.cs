using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MeltySynth
{
    /// <summary>
    /// Reperesents a SoundFont.
    /// </summary>
    public sealed class SoundFont : IDisposable
    {
        private SoundFontInfo info = null!;
        private int bitsPerSample;
        private SoundFontSampleData sampleData = null!;
        private SampleHeader[] sampleHeaders = null!;
        private Preset[] presets = null!;
        private Instrument[] instruments = null!;
        private int refCount = 1;

        /// <summary>
        /// Loads a SoundFont from the stream.
        /// </summary>
        /// <param name="stream">
        /// The data stream used to load the SoundFont.
        /// </param>
        public SoundFont(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            Load(stream, memoryMapFileStream: null);

            Debug.Assert(info != null);
            Debug.Assert(sampleData != null);
            Debug.Assert(sampleHeaders != null);
            Debug.Assert(presets != null);
            Debug.Assert(instruments != null);
        }

        /// <summary>
        /// Loads a SoundFont from the file using memory-mapped sample data for fast startup and minimal RAM usage.
        /// </summary>
        /// <param name="path">
        /// The SoundFont file name and path.
        /// </param>
        public SoundFont(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            try
            {
                Load(stream, memoryMapFileStream: stream);
            }
            catch
            {
                try { sampleData?.Dispose(); } catch { }
                try { stream.Dispose(); } catch { }
                throw;
            }

            Debug.Assert(info != null);
            Debug.Assert(sampleData != null);
            Debug.Assert(sampleHeaders != null);
            Debug.Assert(presets != null);
            Debug.Assert(instruments != null);
        }

        private void Load(Stream stream, FileStream? memoryMapFileStream)
        {
            using (var reader = new BinaryReader(stream, Encoding.ASCII, true))
            {
                var chunkId = reader.ReadFourCC();
                if (chunkId != "RIFF")
                {
                    throw new InvalidDataException("The RIFF chunk was not found.");
                }

                var size = reader.ReadInt32();

                var formType = reader.ReadFourCC();
                if (formType != "sfbk")
                {
                    throw new InvalidDataException($"The type of the RIFF chunk must be 'sfbk', but was '{formType}'.");
                }

                info = new SoundFontInfo(reader);

                sampleData = new SoundFontSampleData(reader, deferMemoryMap: memoryMapFileStream != null);
                bitsPerSample = sampleData.BitsPerSample;

                var parameters = new SoundFontParameters(reader);
                sampleHeaders = parameters.SampleHeaders;
                presets = parameters.Presets;
                instruments = parameters.Instruments;
            }

            if (memoryMapFileStream != null)
            {
                sampleData.AttachMemoryMappedFile(memoryMapFileStream);
            }

            CheckSamples();
            CheckRegions();
        }

        /// <summary>
        /// Gets the name of the SoundFont.
        /// </summary>
        /// <returns>
        /// The name of the SoundFont.
        /// </returns>
        public override string ToString()
        {
            return info.BankName;
        }

        private void CheckSamples()
        {
            var sampleCount = sampleData.Length - 4; // This offset is to ensure that out of range access is safe.

            foreach (var sample in sampleHeaders)
            {
                if (!(0 <= sample.Start && sample.Start < sampleCount))
                {
                    throw new InvalidDataException($"The start position of the sample '{sample.Name}' is out of range.");
                }
                if (!(0 <= sample.StartLoop && sample.StartLoop < sampleCount))
                {
                    throw new InvalidDataException($"The loop start position of the sample '{sample.Name}' is out of range.");
                }
                if (!(0 < sample.End && sample.End <= sampleCount))
                {
                    throw new InvalidDataException($"The end position of the sample '{sample.Name}' is out of range.");
                }
                if (!(0 <= sample.EndLoop && sample.EndLoop <= sampleCount))
                {
                    throw new InvalidDataException($"The loop end position of the sample '{sample.Name}' is out of range.");
                }
            }
        }

        private void CheckRegions()
        {
            var sampleCount = sampleData.Length - 4; // This offset is to ensure that out of range access is safe.

            foreach (var instrument in instruments)
            {
                foreach (var region in instrument.RegionArray)
                {
                    if (!(0 <= region.SampleStart && region.SampleStart < sampleCount))
                    {
                        throw new InvalidDataException($"The start position of the sample '{region.Sample.Name}' in the instrument '{instrument.Name}' is out of range.");
                    }
                    if (!(0 <= region.SampleStartLoop && region.SampleStartLoop < sampleCount))
                    {
                        throw new InvalidDataException($"The loop start position of the sample '{region.Sample.Name}' in the instrument '{instrument.Name}' is out of range.");
                    }
                    if (!(0 < region.SampleEnd && region.SampleEnd <= sampleCount))
                    {
                        throw new InvalidDataException($"The end position of the sample '{region.Sample.Name}' in the instrument '{instrument.Name}' is out of range.");
                    }
                    if (!(0 <= region.SampleEndLoop && region.SampleEndLoop <= sampleCount))
                    {
                        throw new InvalidDataException($"The loop end position of the sample '{region.Sample.Name}' in the instrument '{instrument.Name}' is out of range.");
                    }

                    switch (region.SampleModes)
                    {
                        case LoopMode.NoLoop:
                        case LoopMode.Continuous:
                        case LoopMode.LoopUntilNoteOff:
                            break;
                        default:
                            throw new InvalidDataException($"The sample '{region.Sample.Name}' in the instrument '{instrument.Name}' has an invalid loop mode.");
                    }
                }
            }
        }

        internal bool TryAddRef()
        {
            while (true)
            {
                int current = Volatile.Read(ref refCount);
                if (current <= 0)
                    return false;
                if (Interlocked.CompareExchange(ref refCount, current + 1, current) == current)
                    return true;
            }
        }

        public void Dispose()
        {
            while (true)
            {
                int current = Volatile.Read(ref refCount);
                if (current <= 0)
                    return;
                if (Interlocked.CompareExchange(ref refCount, current - 1, current) == current)
                {
                    if (current == 1)
                    {
                        sampleData?.Dispose();
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// The information of the SoundFont.
        /// </summary>
        public SoundFontInfo Info => info;

        /// <summary>
        /// The bits per sample of the sample data.
        /// </summary>
        /// <remarks>
        /// This value is always 16.
        /// </remarks>
        public int BitsPerSample => bitsPerSample;

        /// <summary>
        /// The sample data.
        /// </summary>
        public ReadOnlySpan<short> WaveData => sampleData.AsSpan();

        /// <summary>
        /// The samples of the SoundFont.
        /// </summary>
        public IReadOnlyList<SampleHeader> SampleHeaders => sampleHeaders;

        /// <summary>
        /// The presets of the SoundFont.
        /// </summary>
        public IReadOnlyList<Preset> Presets => presets;

        /// <summary>
        /// The instruments of the SoundFont.
        /// </summary>
        public IReadOnlyList<Instrument> Instruments => instruments;

        // Internally exposes the raw arrays/buffers for fast enumeration.
        internal SoundFontSampleData SampleData => sampleData;
        internal SampleHeader[] SampleHeaderArray => sampleHeaders;
        internal Preset[] PresetArray => presets;
        internal Instrument[] InstrumentArray => instruments;
    }
}
