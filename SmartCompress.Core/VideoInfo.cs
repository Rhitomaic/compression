namespace SmartCompress.Core;

public record VideoInfo(
    int Width,
    int Height,
    double Fps,
    int BitrateKbps,
    double DurationS,
    double Bppf,
    string Complexity,
    string ComplexityHint
);
