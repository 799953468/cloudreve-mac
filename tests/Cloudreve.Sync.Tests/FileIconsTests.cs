using Cloudreve.Desktop;
using Xunit;

namespace Cloudreve.Sync.Tests;

public sealed class FileIconsTests
{
    [Theory]
    [InlineData("src/Program.cs", "Code", "CS")]
    [InlineData("site/index.HTML", "Code", "HTML")]
    [InlineData("app.js", "Code", "JS")]
    [InlineData("App.tsx", "Code", "TSX")]
    [InlineData("main.swift", "Code", "SWFT")]
    [InlineData("Dockerfile", "Code", "DOCK")]
    [InlineData(".gitignore", "Code", "GIT")]
    [InlineData(".env.local", "Code", "ENV")]
    [InlineData("photo.jpeg", "Image", "JPG")]
    [InlineData("clip.mov", "Video", "MOV")]
    [InlineData("song.flac", "Audio", "FLAC")]
    [InlineData("backup.tar.gz", "Archive", "GZ")]
    [InlineData("Report.pdf", "Document", "PDF")]
    [InlineData("notes.markdown", "Document", "MD")]
    [InlineData("budget.xlsx", "Spreadsheet", "XLSX")]
    [InlineData("talk.key", "Presentation", "KEY")]
    [InlineData("font.woff2", "Font", "WOF2")]
    [InlineData("part.stl", "Model", "STL")]
    [InlineData("data.qwe", "Generic", "QWE")]
    [InlineData("archive.verylongext", "Generic", null)]
    [InlineData("NOTES", "Generic", null)]
    public void StyleFollowsFileType(string name, string kind, string? label)
    {
        var style = FileIcons.StyleOf(name);

        Assert.Equal(Enum.Parse<FileKind>(kind), style.Kind);
        Assert.Equal(label, style.Label);
    }
}
