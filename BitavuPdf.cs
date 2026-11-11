namespace Bitavu;

/// <summary>A rendered PDF and metadata returned by Bitavu.</summary>
public sealed class BitavuPdf
{
    internal BitavuPdf(byte[] bytes, long? revision, int? pageCount, Guid? requestId)
    {
        Bytes = bytes;
        Revision = revision;
        PageCount = pageCount;
        RequestId = requestId;
    }

    /// <summary>The PDF bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>The resolved saved revision, when supplied by the API.</summary>
    public long? Revision { get; }

    /// <summary>The number of PDF pages, when supplied by the API.</summary>
    public int? PageCount { get; }

    /// <summary>The server request identifier, when supplied by the API.</summary>
    public Guid? RequestId { get; }
}
