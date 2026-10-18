using System.Text.Json.Serialization;

namespace ImmichWin.Api;

public enum ThumbSize { Thumbnail, Preview }

public sealed record UserInfo(string Id, string Email, string Name);

public sealed record TagDto(string Id, string Name, string Value);

public sealed record AssetDto
{
    public string Id { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    public string? OriginalMimeType { get; init; }
    public string Type { get; init; } = "IMAGE";
    public string? Checksum { get; init; }
    public DateTimeOffset? FileCreatedAt { get; init; }
    public DateTimeOffset? LocalDateTime { get; init; }
    public bool IsTrashed { get; init; }
    public List<TagDto>? Tags { get; init; }

    [JsonIgnore]
    public DateTimeOffset SortDate => LocalDateTime ?? FileCreatedAt ?? DateTimeOffset.MinValue;

    [JsonIgnore]
    public bool IsRaw
    {
        get
        {
            var ext = Path.GetExtension(OriginalFileName).ToLowerInvariant();
            return ext is ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".raf" or ".orf" or ".rw2" or ".pef" or ".srw";
        }
    }
}

public sealed record AlbumDto(string Id, string AlbumName, int AssetCount, string? AlbumThumbnailAssetId);

public sealed record SearchQuery
{
    public int Page { get; init; } = 1;
    public int Size { get; init; } = 200;
    public DateTimeOffset? CreatedAfter { get; init; }
    public IReadOnlyList<string>? AlbumIds { get; init; }
    public IReadOnlyList<string>? TagIds { get; init; }
    public string Order { get; init; } = "desc";
    public bool WithExif { get; init; }
}

public sealed record SearchPage(IReadOnlyList<AssetDto> Items, string? NextPage);

public sealed record UploadRequest(string FilePath, string DeviceAssetId, DateTimeOffset FileCreatedAt, DateTimeOffset FileModifiedAt);

public sealed record UploadResult(string AssetId, bool Duplicate);

public sealed record DuplicateCheck(string ClientId, string Sha1);

public enum ErrorKind { Unreachable, Unauthorized, NotFound, Server }

public sealed class ImmichException(ErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public ErrorKind Kind { get; } = kind;
}
