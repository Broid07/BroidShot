using System;

namespace BroidShot.Services;

public sealed record HistoryItem(string Id, DateTimeOffset CreatedAt, string ImagePath, int Width, int Height, string? OcrText = null)
{
    public string DisplayName => CreatedAt.ToLocalTime().ToString("dd MMM yyyy · HH:mm:ss");
    public string Dimensions => $"{Width} × {Height}";
}
