namespace WinMusicXmlPlayer.Models
{
    public enum VelocityCurveMode
    {
        Soft = 0,
        Linear = 1,
        Hard = 2
    }

    public sealed class PerformanceOptions
    {
        public int HumanizeAmount { get; set; } = 0;
        public VelocityCurveMode VelocityCurve { get; set; } = VelocityCurveMode.Linear;
        public int DynamicRangePercent { get; set; } = 100;
        public bool AutoPianoPedal { get; set; } = true;
        public bool AutoVibrato { get; set; } = true;
        public bool InstrumentSpatialProfile { get; set; } = true;

        public static PerformanceOptions Default => new();

        public bool RequiresScoreReload(PerformanceOptions other)
        {
            return HumanizeAmount != other.HumanizeAmount ||
                   VelocityCurve != other.VelocityCurve ||
                   DynamicRangePercent != other.DynamicRangePercent ||
                   AutoPianoPedal != other.AutoPianoPedal ||
                   AutoVibrato != other.AutoVibrato;
        }
    }
}
