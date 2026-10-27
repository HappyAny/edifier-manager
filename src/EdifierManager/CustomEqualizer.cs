namespace EdifierManager;

public record EqualizerBand(int Index, int Frequency, int Gain, int Q = 0, int Filter = 0);
public record CustomEqualizer(int Group, int Bank, EqualizerBand[] Bands)
{
    public bool HasQ => Group is 1 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 11 or 13 or 14 or 15;
    public bool AdjustableFrequency => Group is 1 or 4 or 5 or 8 or 9 or 10 or 11 or 13 or 14;
    public bool AdjustableQ => AdjustableFrequency;
    public double MinimumGain => Group switch { 1 or 17 => -6, 4 => -9, 18 => -4, _ => -3 };
    public double MaximumGain => Group switch { 1 or 17 => 6, 18 => 4, _ => 3 };
    public double GainStep => Group is 1 or 4 or 13 or 15 or 17 or 18 ? 1 : .5;
    public int GainMultiplier => Group is 5 or 9 ? 5 : 1;
    public int MinimumQ => Group is 9 or 11 or 13 or 14 ? 7 : 5;
    public double GainDb(int raw) => MinimumGain + raw * GainStep / GainMultiplier;
    public int EncodeGain(double gain)
    {
        double steps = (gain - MinimumGain) / GainStep;
        if (!double.IsFinite(gain) || gain < MinimumGain || gain > MaximumGain || Math.Abs(steps - Math.Round(steps)) > .0001) throw new InvalidDataException("EQ 增益超出范围或不符合步长。");
        return (int)Math.Round(steps) * GainMultiplier;
    }
    public (int Minimum, int Maximum) FrequencyRange(int index) => Group is 9 or 10 or 11 or 14
        ? new[] { (20, 200), (201, 2000), (2001, 4000), (4001, Group is 9 or 11 ? 6000 : 8000) }[index]
        : new[] { (20, 200), (201, 3000), (3001, 5000), (5001, 10000) }[index];
    public static CustomEqualizer? Parse(byte[]? data, WireProfile profile)
    {
        int offset = profile.Version == 2 ? 2 : 0;
        if (data == null || data.Length < offset + 4 || data.Length > 256) return null;
        int group = offset == 0 ? 1 : data[0];
        if (group is < 1 or > 18) return null;
        int count = group switch { 2 or 3 or 7 => 6, 12 or 16 => 9, 17 or 18 => data[1] == 0 ? 10 : data[1], _ => 4 };
        if (count is < 1 or > 16) return null;
        bool six = group is 1 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 11 or 13 or 14 or 15;
        int stride = six ? 6 : 4;
        bool hasBank = group is 7 or 12;
        int bank = hasBank ? data[offset++] : 0;
        if (data.Length < offset + count * stride) return null;
        var bands = new List<EqualizerBand>();
        for (int i = 0; i < count; i++)
        {
            int start = offset + i * stride, frequencyOffset = start + (six ? 2 : 1);
            int frequency = data[frequencyOffset] * 256 + data[frequencyOffset + 1];
            int index = data[start], filter = six ? data[start + 1] : 0;
            if (index >= count || bands.Any(x => x.Index == index) || frequency is < 20 or > 20000 || filter > 1) return null;
            bands.Add(new(index, frequency, data[frequencyOffset + 2], six ? data[frequencyOffset + 3] : 0, filter));
        }
        var eq = new CustomEqualizer(group, bank, bands.OrderBy(x => x.Index).ToArray());
        if (eq.Bands.Any(x => eq.GainDb(x.Gain) < eq.MinimumGain || eq.GainDb(x.Gain) > eq.MaximumGain || x.Gain % eq.GainMultiplier != 0 || six && x.Q is < 5 or > 20)) return null;
        return eq;
    }
    public void ValidateAgainst(CustomEqualizer before)
    {
        if (Group != before.Group || Bank != before.Bank || Bands == null || Bands.Length != before.Bands.Length) throw new InvalidDataException("EQ 配置与当前耳机不匹配。");
        for (int i = 0; i < Bands.Length; i++)
        {
            var band = Bands[i]; var old = before.Bands[i];
            if (band.Index != i || old.Index != i || band.Filter != old.Filter || band.Gain != EncodeGain(GainDb(band.Gain))) throw new InvalidDataException("EQ 频段数据无效。");
            if (AdjustableFrequency)
            { var range = FrequencyRange(i); if (band.Frequency < range.Minimum || band.Frequency > range.Maximum) throw new InvalidDataException($"第 {i + 1} 段频率须在 {range.Minimum}–{range.Maximum} Hz。"); }
            else if (band.Frequency != old.Frequency) throw new InvalidDataException("此 EQ 类型的频率固定。");
            if (AdjustableQ) { if (band.Q < MinimumQ || band.Q > 20) throw new InvalidDataException("Q 值超出范围。"); }
            else if (band.Q != old.Q) throw new InvalidDataException("此 EQ 类型的 Q 值固定。");
        }
    }
    public byte[] EncodeBand(EqualizerBand band)
    {
        byte[] body = HasQ ? [(byte)band.Index, (byte)band.Filter, (byte)(band.Frequency >> 8), (byte)band.Frequency, (byte)band.Gain, (byte)band.Q]
            : [(byte)band.Index, (byte)(band.Frequency >> 8), (byte)band.Frequency, (byte)band.Gain];
        return Group is 7 or 12 ? new[] { (byte)Bank }.Concat(body).ToArray() : body;
    }
    public bool SameBands(CustomEqualizer? other) => other != null && Group == other.Group && Bank == other.Bank && Bands.SequenceEqual(other.Bands);
}
