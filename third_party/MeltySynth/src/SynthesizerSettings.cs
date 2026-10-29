using System;

namespace MeltySynth
{
    /// <summary>
    /// Specifies a set of parameters for synthesis.
    /// </summary>
    public sealed class SynthesizerSettings
    {
        internal static int DefaultBlockSize = 64;
        internal static int DefaultMaximumPolyphony = 64;
        internal static bool DefaultEnableReverbAndChorus = true;

        private int sampleRate;
        private int blockSize;
        private int maximumPolyphony;
        private bool enableReverbAndChorus;
        private float minimumFilterCutoffHz;
        private int filterCutoffFloorMaxProgram;

        /// <summary>
        /// Initializes a new instance of synthesizer settings.
        /// </summary>
        /// <param name="sampleRate">The sample rate for synthesis.</param>
        public SynthesizerSettings(int sampleRate)
        {
            CheckSampleRate(sampleRate);

            this.sampleRate = sampleRate;
            this.blockSize = DefaultBlockSize;
            this.maximumPolyphony = DefaultMaximumPolyphony;
            this.enableReverbAndChorus = DefaultEnableReverbAndChorus;
            this.minimumFilterCutoffHz = 0F;
            this.filterCutoffFloorMaxProgram = 127;
        }

        private static void CheckSampleRate(int value)
        {
            if (!(16000 <= value && value <= 192000))
            {
                throw new ArgumentOutOfRangeException("The sample rate must be between 16000 and 192000.");
            }
        }

        private static void CheckBlockSize(int value)
        {
            if (!(8 <= value && value <= 1024))
            {
                throw new ArgumentOutOfRangeException("The block size must be between 8 and 1024.");
            }
        }

        private static void CheckMaximumPolyphony(int value)
        {
            if (!(8 <= value && value <= 256))
            {
                throw new ArgumentOutOfRangeException("The maximum number of polyphony must be between 8 and 256.");
            }
        }

        /// <summary>
        /// Gets or sets the sample rate for synthesis.
        /// </summary>
        public int SampleRate
        {
            get => sampleRate;

            set
            {
                CheckSampleRate(value);
                sampleRate = value;
            }
        }

        /// <summary>
        /// Gets or sets the block size for rendering waveform.
        /// </summary>
        public int BlockSize
        {
            get => blockSize;

            set
            {
                CheckBlockSize(value);
                blockSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the number of maximum polyphony.
        /// </summary>
        public int MaximumPolyphony
        {
            get => maximumPolyphony;

            set
            {
                CheckMaximumPolyphony(value);
                maximumPolyphony = value;
            }
        }

        /// <summary>
        /// Gets or sets whether reverb and chorus are enabled.
        /// </summary>
        public bool EnableReverbAndChorus
        {
            get => enableReverbAndChorus;
            set => enableReverbAndChorus = value;
        }

        /// <summary>
        /// Gets or sets the minimum low-pass filter cutoff frequency, in Hz.
        /// A value of 0 disables the floor and keeps the SoundFont's original cutoff.
        /// </summary>
        public float MinimumFilterCutoffHz
        {
            get => minimumFilterCutoffHz;

            set
            {
                if (!(0F <= value && value <= 96000F))
                {
                    throw new ArgumentOutOfRangeException("The minimum filter cutoff must be between 0 and 96000 Hz.");
                }

                minimumFilterCutoffHz = value;
            }
        }

        /// <summary>
        /// Gets or sets the highest GM program number the cutoff floor applies to.
        /// Set to 127 to apply the floor to every melodic program.
        /// </summary>
        public int FilterCutoffFloorMaxProgram
        {
            get => filterCutoffFloorMaxProgram;

            set
            {
                if (!(0 <= value && value <= 127))
                {
                    throw new ArgumentOutOfRangeException("The cutoff floor program limit must be between 0 and 127.");
                }

                filterCutoffFloorMaxProgram = value;
            }
        }
    }
}
