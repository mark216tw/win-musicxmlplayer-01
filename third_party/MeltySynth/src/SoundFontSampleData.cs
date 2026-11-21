using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MeltySynth
{
    internal sealed unsafe class SoundFontSampleData : IDisposable
    {
        private readonly int bitsPerSample;
        private readonly short[]? samples;
        private readonly int sampleCount;
        private readonly long smplOffset;
        private readonly long smplByteSize;

        private MemoryMappedFile? mappedFile;
        private MemoryMappedViewAccessor? mappedAccessor;
        private byte* mappedBasePtr;
        private short* mappedSamplesPtr;
        private int disposed;

        internal SoundFontSampleData(BinaryReader reader, bool deferMemoryMap = false)
        {
            var chunkId = reader.ReadFourCC();
            if (chunkId != "LIST")
            {
                throw new InvalidDataException("The LIST chunk was not found.");
            }

            var end = (long)reader.ReadUInt32();
            end += reader.BaseStream.Position;

            var listType = reader.ReadFourCC();
            if (listType != "sdta")
            {
                throw new InvalidDataException($"The type of the LIST chunk must be 'sdta', but was '{listType}'.");
            }

            bool foundSmpl = false;
            Span<byte> header = stackalloc byte[4];

            while (reader.BaseStream.Position < end)
            {
                var id = reader.ReadFourCC();
                long size = reader.ReadUInt32();

                switch (id)
                {
                    case "smpl":
                        bitsPerSample = 16;
                        foundSmpl = true;
                        sampleCount = (int)(size / 2);

                        if (deferMemoryMap)
                        {
                            smplOffset = reader.BaseStream.Position;
                            smplByteSize = size;

                            if (size >= 4)
                            {
                                int read = reader.Read(header);
                                if (read == 4 &&
                                    header[0] == (byte)'O' &&
                                    header[1] == (byte)'g' &&
                                    header[2] == (byte)'g' &&
                                    header[3] == (byte)'S')
                                {
                                    throw new NotSupportedException("SoundFont3 is not yet supported.");
                                }
                                reader.BaseStream.Position = smplOffset + size;
                            }
                            else
                            {
                                reader.BaseStream.Position += size;
                            }
                        }
                        else
                        {
                            samples = new short[sampleCount];
                            reader.Read(MemoryMarshal.Cast<short, byte>(samples));
                            if (samples.Length >= 2 &&
                                Encoding.ASCII.GetString(MemoryMarshal.Cast<short, byte>(samples).Slice(0, 4)) == "OggS")
                            {
                                throw new NotSupportedException("SoundFont3 is not yet supported.");
                            }
                        }
                        break;
                    case "sm24":
                        // 24 bit audio is not supported.
                        reader.BaseStream.Position += size;
                        break;
                    default:
                        throw new InvalidDataException($"The INFO list contains an unknown ID '{id}'.");
                }
            }

            if (!foundSmpl)
            {
                throw new InvalidDataException("No valid sample data was found.");
            }

            if (!BitConverter.IsLittleEndian)
            {
                // TODO: Insert the byte swapping code here.
                throw new NotSupportedException("Big endian architectures are not yet supported.");
            }
        }

        internal void AttachMemoryMappedFile(FileStream fileStream)
        {
            if (samples != null)
            {
                fileStream.Dispose();
                return;
            }

            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? accessor = null;
            byte* basePtr = null;

            try
            {
                mmf = MemoryMappedFile.CreateFromFile(
                    fileStream,
                    mapName: null,
                    capacity: 0,
                    MemoryMappedFileAccess.Read,
                    HandleInheritability.None,
                    leaveOpen: false);

                accessor = mmf.CreateViewAccessor(smplOffset, smplByteSize, MemoryMappedFileAccess.Read);
                accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
                if (basePtr == null)
                {
                    throw new InvalidOperationException("Failed to acquire memory-mapped SoundFont view pointer.");
                }

                mappedFile = mmf;
                mappedAccessor = accessor;
                mappedBasePtr = basePtr;
                mappedSamplesPtr = (short*)(basePtr + accessor.PointerOffset);
            }
            catch
            {
                if (basePtr != null && accessor != null)
                {
                    try { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                }
                try { accessor?.Dispose(); } catch { }
                if (mmf != null)
                {
                    try { mmf.Dispose(); } catch { }
                }
                else
                {
                    try { fileStream.Dispose(); } catch { }
                }
                throw;
            }
        }

        public int BitsPerSample => bitsPerSample;
        public int Length => sampleCount;
        public short[]? ManagedSamples => samples;
        public short* MappedSamplesPointer => mappedSamplesPtr;

        public ReadOnlySpan<short> AsSpan()
        {
            if (samples != null)
            {
                return samples;
            }

            if (Volatile.Read(ref disposed) != 0 || mappedSamplesPtr == null)
            {
                return ReadOnlySpan<short>.Empty;
            }

            return new ReadOnlySpan<short>(mappedSamplesPtr, sampleCount);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            var accessor = mappedAccessor;
            var mmf = mappedFile;
            var basePtr = mappedBasePtr;

            mappedBasePtr = null;
            mappedSamplesPtr = null;
            mappedAccessor = null;
            mappedFile = null;

            if (accessor != null)
            {
                if (basePtr != null)
                {
                    try { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                }
                try { accessor.Dispose(); } catch { }
            }

            if (mmf != null)
            {
                try { mmf.Dispose(); } catch { }
            }

            GC.SuppressFinalize(this);
        }

        ~SoundFontSampleData()
        {
            Dispose();
        }
    }
}
