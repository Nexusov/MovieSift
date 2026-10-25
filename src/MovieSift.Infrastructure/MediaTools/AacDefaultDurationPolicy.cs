using System.Globalization;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Recognizes only a mathematically equivalent AAC-LC DefaultDuration added by the pinned muxer.</summary>
internal static class AacDefaultDurationPolicy
{
    internal const string PropertyName = "default_duration";
    private const string AacCodecIdentifier = "A_AAC";
    private const int MaximumConfigurationBytes = 32;
    private const int HexCharactersPerByte = 2;
    private const int BitsPerByte = 8;
    private const int AacLowComplexityObjectType = 2;
    private const int AudioObjectTypeShift = 11;
    private const int SamplingIndexShift = 7;
    private const int ChannelConfigurationShift = 3;
    private const int FourBitMask = 15;
    private const int GeneralAudioFlagsMask = 7;
    private const int MinimumChannelConfiguration = 1;
    private const int MaximumChannelConfiguration = 7;
    private const int SamplesPerFrame = 1024;
    private const decimal NanosecondsPerSecond = 1_000_000_000;
    private const decimal IntegerRoundingToleranceNanoseconds = 1;
    private const int BasicConfigurationBits = 16;
    private const int SyncExtensionBits = 11;
    private const int SyncExtensionIdentifier = 0x2b7;
    private const int AudioObjectTypeBits = 5;
    private const int SbrObjectType = 5;
    private const int SbrPresenceBits = 1;
    private static readonly int[] SamplingFrequencies =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    /// <summary>Allows an absent-to-present header only when unchanged codec configuration proves its duration.</summary>
    internal static bool PermitsAddition(TrackSemanticComparison request)
    {
        var expected = request.Expected;
        var actual = request.Actual;
        var eligibleTracks = expected.Kind == TrackKind.Audio && actual.Kind == TrackKind.Audio
            && expected.Properties.GetValueOrDefault("codec_id") == AacCodecIdentifier
            && actual.Properties.GetValueOrDefault("codec_id") == AacCodecIdentifier;

        if (!eligibleTracks || expected.Properties.ContainsKey(PropertyName))
        {
            return false;
        }

        var configuration = expected.Properties.GetValueOrDefault("codec_private_data");
        var samplingFrequencyText = expected.Properties.GetValueOrDefault("audio_sampling_frequency");
        var unchangedConfiguration = configuration is not null
            && configuration == actual.Properties.GetValueOrDefault("codec_private_data")
            && samplingFrequencyText == actual.Properties.GetValueOrDefault("audio_sampling_frequency");

        var samplingFrequencyIsValid = int.TryParse(samplingFrequencyText, NumberStyles.None,
            CultureInfo.InvariantCulture, out var samplingFrequency);
        var durationText = actual.Properties.GetValueOrDefault(PropertyName);
        var durationIsValid = ulong.TryParse(durationText, NumberStyles.None, CultureInfo.InvariantCulture, out var actualDuration);
        var validKnownTiming = unchangedConfiguration && samplingFrequencyIsValid && durationIsValid;

        if (!validKnownTiming)
        {
            return false;
        }

        if (!IsSupportedConfiguration(new AacConfiguration(configuration!, samplingFrequency)))
        {
            return false;
        }

        // DefaultDuration is integer nanoseconds; only sub-nanosecond division rounding is accepted.
        // Full frame counts and timestamps remain independently compared by MkvBackend.VerifyAsync.
        var expectedDuration = SamplesPerFrame * NanosecondsPerSecond / samplingFrequency;
        return Math.Abs(actualDuration - expectedDuration) <= IntegerRoundingToleranceNanoseconds;
    }

    /// <summary>Recognizes the fixed-size AAC-LC configuration prefix and rejects ambiguous frame-length extensions.</summary>
    private static bool IsSupportedConfiguration(AacConfiguration request)
    {
        var maximumHexCharacters = MaximumConfigurationBytes * HexCharactersPerByte;
        var minimumHexCharacters = BasicConfigurationBits / BitsPerByte * HexCharactersPerByte;
        var configurationLengthIsValid = request.Hex.Length >= minimumHexCharacters
            && request.Hex.Length <= maximumHexCharacters && request.Hex.Length % HexCharactersPerByte == 0;

        if (!configurationLengthIsValid)
        {
            return false;
        }

        byte[] configuration;

        try
        {
            configuration = Convert.FromHexString(request.Hex);
        }
        catch (FormatException)
        {
            return false;
        }

        var prefix = configuration[0] << BitsPerByte | configuration[1];
        var objectType = prefix >> AudioObjectTypeShift;
        var samplingIndex = prefix >> SamplingIndexShift & FourBitMask;
        var channelConfiguration = prefix >> ChannelConfigurationShift & FourBitMask;
        var supportedPrefix = objectType == AacLowComplexityObjectType && samplingIndex < SamplingFrequencies.Length
            && channelConfiguration is >= MinimumChannelConfiguration and <= MaximumChannelConfiguration
            && (prefix & GeneralAudioFlagsMask) == 0;

        if (!supportedPrefix || SamplingFrequencies[samplingIndex] != request.SamplingFrequency)
        {
            return false;
        }

        // ISO/IEC 14496-3 ASC: object type, sampling index, channels, then the three GA flags.
        // Reference implementation: https://ffmpeg.org/doxygen/7.1/mpeg4audio_8c_source.html
        var cursor = BasicConfigurationBits;

        if (RemainingBitsAreZero(new ConfigurationBits(configuration, cursor)))
        {
            return true;
        }

        var requiredExtensionBits = SyncExtensionBits + AudioObjectTypeBits + SbrPresenceBits;

        if (configuration.Length * BitsPerByte - cursor < requiredExtensionBits)
        {
            return false;
        }

        var extensionIdentifier = ReadBits(new BitField(configuration, cursor, SyncExtensionBits));
        cursor += SyncExtensionBits;
        var extensionObjectType = ReadBits(new BitField(configuration, cursor, AudioObjectTypeBits));
        cursor += AudioObjectTypeBits;
        var sbrPresent = ReadBits(new BitField(configuration, cursor, SbrPresenceBits));
        cursor += SbrPresenceBits;
        return extensionIdentifier == SyncExtensionIdentifier && extensionObjectType == SbrObjectType
            && sbrPresent == 0 && RemainingBitsAreZero(new ConfigurationBits(configuration, cursor));
    }

    /// <summary>Reads only a bounded ASC bit field using its explicit most-significant-bit ordering.</summary>
    private static int ReadBits(BitField request)
    {
        var value = 0;

        for (var offset = 0; offset < request.Count; offset++)
        {
            var position = request.Start + offset;
            var bit = request.Bytes[position / BitsPerByte] >> (BitsPerByte - 1 - position % BitsPerByte) & 1;
            value = value << 1 | bit;
        }

        return value;
    }

    /// <summary>Accepts padding only after the completely recognized codec configuration.</summary>
    private static bool RemainingBitsAreZero(ConfigurationBits request)
    {

        for (var position = request.Start; position < request.Bytes.Length * BitsPerByte; position++)
        {

            if (ReadBits(new BitField(request.Bytes, position, 1)) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record AacConfiguration(string Hex, int SamplingFrequency);
    private sealed record ConfigurationBits(byte[] Bytes, int Start);
    private sealed record BitField(byte[] Bytes, int Start, int Count);
}
