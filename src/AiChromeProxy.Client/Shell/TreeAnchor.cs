namespace AiChromeProxy.Client.Shell;

/// <summary>Where a menu opened from the keyboard goes (just under a row) and how big the viewport is.</summary>
public sealed record TreeAnchor(double X, double Y, int ViewportWidth, int ViewportHeight);
