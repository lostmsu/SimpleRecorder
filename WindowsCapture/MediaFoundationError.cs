using System.Runtime.InteropServices;

using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace CaptureEncoder;

public enum MediaFoundationError
{
    MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212),
    MF_E_TRANSCODE_NO_MATCHING_ENCODER = unchecked((int)0xC00DA412),
    MF_E_TRANSFORM_TYPE_NOT_SET = unchecked((int)0xC00D6D60),
    MF_E_ATTRIBUTENOTFOUND = unchecked((int)0xC00D36E6),
}

public static class ComExceptionExtensions
{
    public static MediaFoundationError MF(this COMException error)
        => (MediaFoundationError)error.ErrorCode;

    public static Exception? Rethrow(this COMException error,
                                     MediaTranscoder? transcoder = null,
                                     MediaEncodingProfile? encodingProfile = null)
    {
        var mf = error.MF();
        switch (mf)
        {
            case MediaFoundationError.MF_E_TRANSCODE_NO_MATCHING_ENCODER:
                string hwOrSw = transcoder?.HardwareAccelerationEnabled == true ? "HW" : "SW";
                return new NotSupportedException($"Unable to find encoder for {encodingProfile?.Audio?.Subtype} or {hwOrSw} encoder for {encodingProfile?.Video?.Subtype}", error);
            case MediaFoundationError.MF_E_TRANSFORM_TYPE_NOT_SET:
                return new InvalidOperationException("Transform type not set", error);
            case MediaFoundationError.MF_E_ATTRIBUTENOTFOUND:
                return new NotSupportedException("Attribute not found", error);
            default:
                return null;
        }
    }
}
