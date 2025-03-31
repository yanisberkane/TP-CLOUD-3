namespace SharedEvents.Events;

public class PostCreatedEvent
{
    public Guid PostId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public Guid BlobImage { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class CommentCreatedEvent
{
    public Guid CommentId { get; set; }
    public Guid PostId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public DateTime Created { get; set; }
}

public class ImageResizedEvent
{
    public Guid PostId { get; set; }
    public Guid BlobImage { get; set; }
    public string ResizedUrl { get; set; } = string.Empty;
}

public class ContentValidatedEvent
{
    public Guid PostId { get; set; }
    public Guid BlobImage { get; set; }
    public bool IsValid { get; set; }
    public string? Reason { get; set; }
}

public class SavePostToDbEvent
{
    public Guid PostId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public Guid BlobImage { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool? IsContentValid { get; set; }
    public string? ValidationReason { get; set; }
    public string? ResizedUrl { get; set; }
}

public class SaveCommentToDbEvent
{
    public Guid CommentId { get; set; }
    public Guid PostId { get; set; }
    public string Content { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public DateTime Created { get; set; }
}
