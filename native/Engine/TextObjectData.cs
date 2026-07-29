namespace VectorAnimationEngine;

internal enum TextFontStyle : byte
{
    Regular,
    Bold,
    Italic,
    BoldItalic
}

internal enum TextHorizontalAlignment : byte
{
    Left,
    Center,
    Right
}

internal sealed record TextObjectData(
    string Content,
    string FontFamilyName,
    float FontSizePoints,
    TextFontStyle FontStyle,
    TextHorizontalAlignment Alignment,
    SizeF LayoutSize);
