using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Cloudreve.Desktop;

internal enum FileKind
{
    Generic,
    Image,
    Video,
    Audio,
    Archive,
    Code,
    Document,
    Spreadsheet,
    Presentation,
    Font,
    Model
}

internal sealed record FileIconStyle(FileKind Kind, string? Label, Color Color);

/// <summary>
/// File-type icons for activity rows: a page whose glyph shows the category, plus a colored label
/// naming the exact format (CS, HTML, JS, PDF, ...).
/// </summary>
internal static class FileIcons
{
    // Page outline with a folded corner; glyphs are drawn inside the page's hole (even-odd fill)
    // and stay above y=19.5 so the label can overlap the page's bottom edge.
    private const string Page = "M4 1h10l6 6v16H4z M6 3v18h12V9h-6V3z";
    private const int MaxLabel = 4;
    private static readonly Color UnknownColor = Color.Parse("#78909C");

    private static readonly Dictionary<FileKind, string> GeometryPaths = new()
    {
        [FileKind.Generic] = Page,
        [FileKind.Image] = Page + " M8 19l3-4.5 2 2.5 1.5-2L17 19z M15 10.8a1.3 1.3 0 1 0 0 2.6a1.3 1.3 0 1 0 0-2.6z",
        [FileKind.Video] = Page + " M10 11.5v7l6-3.5z",
        [FileKind.Audio] = Page + " M12.5 11h3.5v2h-2v4.5a2 2 0 1 1-1.5-1.94z",
        [FileKind.Archive] = Page + " M10 10h2v1.5h-2z M12 11.5h2V13h-2z M10 13h2v1.5h-2z M12 14.5h2V16h-2z M10 16.5h4v3h-4z M11 17.3v1.4h2v-1.4z",
        [FileKind.Code] = Page + " M10.5 12l-3 3 3 3 1-1-2-2 2-2z M13.5 12l3 3-3 3-1-1 2-2-2-2z",
        [FileKind.Document] = Page + " M8 11.5h8V13H8z M8 14.5h8V16H8z M8 17.5h5V19H8z",
        [FileKind.Spreadsheet] = Page + " M8 11h3.5v2.5H8z M12.5 11H16v2.5h-3.5z M8 14.5h3.5V17H8z M12.5 14.5H16V17h-3.5z M8 18h3.5v1.5H8z M12.5 18H16v1.5h-3.5z",
        [FileKind.Presentation] = Page + " M8 15h2v4H8z M11 12h2v7h-2z M14 14h2v5h-2z",
        [FileKind.Font] = Page + " M11 11h2l3.5 8h-2l-.8-2h-3.4l-.8 2h-2z M10.9 15.5h2.2L12 12.8z",
        [FileKind.Model] = Page + " M12 10l4.5 2.5V17L12 19.5 7.5 17v-4.5z M8.5 13.6v3l3 1.7v-3z M15.5 13.6l-3 1.7v3l3-1.7z"
    };

    // Short labels for extensions that are longer than the label badge allows.
    private static readonly Dictionary<string, string> LabelOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["markdown"] = "MD", ["numbers"] = "NUM", ["pages"] = "PAGE", ["ipynb"] = "IPYN", ["svelte"] = "SVLT", ["astro"] = "ASTR",
        ["graphql"] = "GQL", ["groovy"] = "GRVY", ["gradle"] = "GRDL", ["dockerfile"] = "DOCK", ["makefile"] = "MAKE", ["gitignore"] = "GIT",
        ["gitattributes"] = "GIT", ["gitmodules"] = "GIT", ["editorconfig"] = "CFG", ["appimage"] = "APP", ["torrent"] = "TORR",
        ["sqlite3"] = "SQLT", ["sqlite"] = "SQLT", ["sketch"] = "SKCH", ["blend"] = "BLND", ["woff2"] = "WOF2", ["xcconfig"] = "XCCF",
        ["csproj"] = "PROJ", ["fsproj"] = "PROJ", ["vbproj"] = "PROJ", ["vcxproj"] = "PROJ", ["props"] = "PROP", ["targets"] = "TGTS",
        ["entitlements"] = "ENT", ["storyboard"] = "SB", ["xcworkspace"] = "XCWS", ["pbxproj"] = "XCPJ", ["cmake"] = "CMAK", ["scala"] = "SCLA",
        ["swift"] = "SWFT", ["kotlin"] = "KT", ["clojure"] = "CLJ", ["haskell"] = "HS", ["jsonc"] = "JSNC", ["json5"] = "JSN5", ["jsonl"] = "JSNL",
        ["ndjson"] = "NDJS", ["coffee"] = "COFF", ["razor"] = "RZR", ["cshtml"] = "CSHT", ["handlebars"] = "HBS", ["mustache"] = "MSTC",
        ["prisma"] = "PRSM", ["proto"] = "PROT", ["thrift"] = "THFT", ["nginx"] = "NGNX", ["properties"] = "PROP", ["plist"] = "PLST",
        ["license"] = "LIC", ["readme"] = "READ", ["jpeg"] = "JPG", ["mpeg"] = "MPG", ["xhtml"] = "XHTM", ["psm1"] = "PS1", ["psd1"] = "PS1",
        ["webloc"] = "LINK", ["url"] = "LINK", ["desktop"] = "LINK", ["keynote"] = "KEY"
    };

    // Exact file names (no extension or dot files) that still deserve a specific icon.
    private static readonly Dictionary<string, string> NamedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile", ["Containerfile"] = "dockerfile", ["Makefile"] = "makefile", ["GNUmakefile"] = "makefile",
        ["CMakeLists.txt"] = "cmake", ["Gemfile"] = "rb", ["Rakefile"] = "rb", ["Podfile"] = "rb", ["Brewfile"] = "rb",
        ["Vagrantfile"] = "rb", ["Jenkinsfile"] = "groovy", ["Procfile"] = "yaml", ["LICENSE"] = "license",
        ["LICENCE"] = "license", ["COPYING"] = "license", ["README"] = "readme", [".gitignore"] = "gitignore",
        [".gitattributes"] = "gitattributes", [".gitmodules"] = "gitmodules", [".editorconfig"] = "editorconfig",
        [".env"] = "env", [".dockerignore"] = "dockerfile", [".npmrc"] = "conf", [".bashrc"] = "bash",
        [".zshrc"] = "zsh", [".profile"] = "sh", [".bash_profile"] = "bash", [".prettierrc"] = "json",
        [".eslintrc"] = "json", [".babelrc"] = "json"
    };

    private static readonly Dictionary<string, (FileKind Kind, Color Color)> Styles = Build();

    internal static FileIconStyle StyleOf(string fileName)
    {
        string name = Path.GetFileName(fileName);
        if (NamedFiles.TryGetValue(name, out var alias) && Styles.TryGetValue(alias, out var named))
        {
            return new FileIconStyle(named.Kind, LabelFor(alias), named.Color);
        }

        string extension = Path.GetExtension(name);
        if (extension.Length <= 1)
        {
            return new FileIconStyle(FileKind.Generic, null, UnknownColor);
        }

        extension = name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ? "env" : extension[1..];
        if (Styles.TryGetValue(extension, out var style))
        {
            return new FileIconStyle(style.Kind, LabelFor(extension), style.Color);
        }

        // Unknown but short, readable extensions still get a neutral label.
        bool readable = extension.Length <= MaxLabel && extension.All(char.IsAsciiLetterOrDigit);
        return new FileIconStyle(FileKind.Generic, readable ? extension.ToUpperInvariant() : null, UnknownColor);
    }

    // Parsed on first use: geometry needs Avalonia's render platform, which is not ready during type initialization.
    private static readonly Dictionary<FileKind, Geometry> Geometries = [];

    internal static Geometry GeometryOf(FileKind kind)
    {
        if (!Geometries.TryGetValue(kind, out var geometry))
        {
            geometry = StreamGeometry.Parse(GeometryPaths[kind]);
            Geometries[kind] = geometry;
        }
        return geometry;
    }

    /// <summary>Builds the icon control shown at the start of an activity row.</summary>
    internal static Control Create(string fileName, bool active)
    {
        var style = StyleOf(fileName);
        // Centered in the row like the other activity icons; the label sits on the page's bottom-right corner.
        var icon = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new PathIcon
        {
            Width = 22, Height = 26, Classes = { active ? "activityInk" : "mutedIcon" }, Data = GeometryOf(style.Kind),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 1, 0, 0)
        });
        if (style.Label != null)
        {
            icon.Children.Add(new Border
            {
                Classes = { "fileBadge" }, Background = new SolidColorBrush(style.Color), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(2, 0), MinWidth = 14,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = style.Label, FontSize = 7.5, FontWeight = FontWeight.Bold, LineHeight = 10,
                    TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(TextOn(style.Color))
                }
            });
        }
        return icon;
    }

    private static string LabelFor(string extension)
    {
        if (LabelOverrides.TryGetValue(extension, out var label))
        {
            return label;
        }
        return extension.Length <= MaxLabel ? extension.ToUpperInvariant() : extension[..MaxLabel].ToUpperInvariant();
    }

    private static Color TextOn(Color background)
    {
        double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return luminance > 0.62 ? Color.Parse("#1F2328") : Colors.White;
    }

    private static Dictionary<string, (FileKind, Color)> Build()
    {
        var map = new Dictionary<string, (FileKind, Color)>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string color, params string[] extensions)
        {
            var parsed = Color.Parse(color);
            foreach (string extension in extensions)
            {
                map.TryAdd(extension, (kind, parsed));
            }
        }

        // Images
        Add(FileKind.Image, "#26A269", "png", "apng");
        Add(FileKind.Image, "#2E7D32", "jpg", "jpeg", "jpe", "jfif");
        Add(FileKind.Image, "#00897B", "gif");
        Add(FileKind.Image, "#00838F", "webp", "avif", "jxl");
        Add(FileKind.Image, "#558B2F", "heic", "heif");
        Add(FileKind.Image, "#43A047", "bmp", "tif", "tiff", "tga", "pcx", "ppm", "pgm", "pbm", "dds");
        Add(FileKind.Image, "#6D4C41", "raw", "cr2", "cr3", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "pef", "x3f", "srw");
        Add(FileKind.Image, "#00796B", "ico", "icns", "cur");
        Add(FileKind.Image, "#FFB13B", "svg", "svgz");
        Add(FileKind.Image, "#5E35B1", "exr", "hdr");
        Add(FileKind.Image, "#31A8FF", "psd", "psb");
        Add(FileKind.Image, "#FF9A00", "ai");
        Add(FileKind.Image, "#D35400", "eps");
        Add(FileKind.Image, "#FF61F6", "xd");
        Add(FileKind.Image, "#F7B500", "sketch");
        Add(FileKind.Image, "#A259FF", "fig");
        Add(FileKind.Image, "#00C4CC", "afphoto", "afdesign");
        Add(FileKind.Image, "#3E7C17", "xcf", "kra");
        Add(FileKind.Image, "#0E7C86", "drawio", "vsdx", "vsd");

        // Video
        Add(FileKind.Video, "#E53935", "mp4", "m4v", "mp4v");
        Add(FileKind.Video, "#C62828", "mov", "qt");
        Add(FileKind.Video, "#AD1457", "mkv", "mk3d");
        Add(FileKind.Video, "#D81B60", "avi", "divx", "xvid");
        Add(FileKind.Video, "#EF6C00", "webm", "ogv");
        Add(FileKind.Video, "#B71C1C", "wmv", "asf", "flv", "f4v", "swf", "rm", "rmvb");
        Add(FileKind.Video, "#C2185B", "mpg", "mpeg", "mpe", "m2v", "vob", "m2ts", "mts", "3gp", "3g2", "dv", "mxf");
        Add(FileKind.Video, "#880E4F", "srt", "ass", "ssa", "vtt", "sub");
        Add(FileKind.Video, "#4A148C", "prproj", "aep", "fcpxml", "veg", "drp");

        // Audio
        Add(FileKind.Audio, "#7B1FA2", "mp3", "mp2");
        Add(FileKind.Audio, "#6A1B9A", "wav", "wave", "bwf");
        Add(FileKind.Audio, "#512DA8", "flac", "alac", "ape", "wv", "tta", "dsf", "dff");
        Add(FileKind.Audio, "#8E24AA", "aac", "m4a", "m4b", "m4r");
        Add(FileKind.Audio, "#9C27B0", "ogg", "oga", "opus", "spx");
        Add(FileKind.Audio, "#673AB7", "wma", "aiff", "aif", "aifc", "caf", "amr", "au", "snd");
        Add(FileKind.Audio, "#AB47BC", "mid", "midi", "kar", "mod", "xm", "it", "s3m");
        Add(FileKind.Audio, "#4527A0", "als", "flp", "logicx", "band", "ptx", "cpr", "sesx", "aup", "aup3");
        Add(FileKind.Audio, "#7E57C2", "cue", "m3u", "m3u8", "pls");

        // Archives, disk images and installers
        Add(FileKind.Archive, "#8D6E63", "zip", "zipx");
        Add(FileKind.Archive, "#6D4C41", "rar");
        Add(FileKind.Archive, "#5D4037", "7z");
        Add(FileKind.Archive, "#795548", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz", "zst", "lz", "lz4", "lzma", "z", "cpio", "cab", "arj", "lzh", "ace", "sit", "sitx", "br");
        Add(FileKind.Archive, "#546E7A", "iso", "img", "vhd", "vhdx", "vmdk", "qcow2", "vdi", "toast");
        Add(FileKind.Archive, "#607D8B", "dmg", "pkg", "mpkg", "sparseimage", "sparsebundle");
        Add(FileKind.Archive, "#455A64", "exe", "msi", "msix", "appx", "com", "dll", "sys", "so", "dylib", "bin", "run", "appimage", "o", "a", "lib");
        Add(FileKind.Archive, "#A80030", "deb");
        Add(FileKind.Archive, "#EE0000", "rpm");
        Add(FileKind.Archive, "#3DDC84", "apk", "aab", "xapk");
        Add(FileKind.Archive, "#147EFB", "ipa");
        Add(FileKind.Archive, "#B07219", "jar", "war", "ear");
        Add(FileKind.Archive, "#2E7D32", "torrent");
        Add(FileKind.Archive, "#37474F", "bak", "tmp", "old", "swp", "part", "crdownload", "download");

        // Documents
        Add(FileKind.Document, "#E53935", "pdf");
        Add(FileKind.Document, "#2B579A", "doc", "docx", "docm", "dot", "dotx", "odt", "ott", "wps", "wpd");
        Add(FileKind.Document, "#1565C0", "rtf", "rtfd");
        Add(FileKind.Document, "#FF9500", "pages");
        Add(FileKind.Document, "#78909C", "txt", "text");
        Add(FileKind.Document, "#083FA1", "md", "markdown", "mdown", "mkd", "rst", "adoc", "asciidoc", "org");
        Add(FileKind.Document, "#1B1F24", "mdx");
        Add(FileKind.Document, "#90A4AE", "log", "out");
        Add(FileKind.Document, "#008080", "tex", "latex", "bib", "cls", "sty");
        Add(FileKind.Document, "#86B918", "epub");
        Add(FileKind.Document, "#FF9900", "mobi", "azw", "azw3", "kfx");
        Add(FileKind.Document, "#5D4037", "djvu", "fb2", "cbz", "cbr", "xps", "oxps", "chm");
        Add(FileKind.Document, "#0078D4", "eml", "msg", "mbox", "emlx");
        Add(FileKind.Document, "#7E57C2", "one", "onetoc2", "enex");
        Add(FileKind.Document, "#E64A19", "ics", "ical", "vcs");
        Add(FileKind.Document, "#00897B", "vcf", "vcard");
        Add(FileKind.Document, "#3F51B5", "webloc", "url", "desktop", "lnk", "alias");
        Add(FileKind.Document, "#455A64", "license", "readme", "nfo", "diz");
        Add(FileKind.Document, "#00695C", "pem", "crt", "cer", "der", "p12", "pfx", "csr", "pub", "asc", "gpg", "sig");

        // Spreadsheets and data
        Add(FileKind.Spreadsheet, "#217346", "xls", "xlsx", "xlsm", "xlsb", "xlt", "xltx", "ods", "ots");
        Add(FileKind.Spreadsheet, "#34C759", "numbers");
        Add(FileKind.Spreadsheet, "#1D6F42", "csv", "tsv", "tab", "dif", "slk");
        Add(FileKind.Spreadsheet, "#003B57", "db", "sqlite", "sqlite3", "db3", "mdb", "accdb", "dbf", "frm", "ibd", "realm");
        Add(FileKind.Spreadsheet, "#150458", "parquet", "feather", "arrow", "orc", "avro", "pkl", "pickle", "npy", "npz", "h5", "hdf5", "mat", "sav", "dta", "rds", "rdata");

        // Presentations
        Add(FileKind.Presentation, "#D24726", "ppt", "pptx", "pptm", "pps", "ppsx", "pot", "potx", "odp", "otp");
        Add(FileKind.Presentation, "#0A84FF", "key", "keynote");

        // Fonts
        Add(FileKind.Font, "#C0392B", "ttf", "ttc");
        Add(FileKind.Font, "#A93226", "otf", "otc");
        Add(FileKind.Font, "#E74C3C", "woff", "woff2", "eot", "fon", "fnt", "pfb", "pfm", "dfont", "bdf", "pcf");

        // 3D, CAD and game assets
        Add(FileKind.Model, "#F5792A", "blend", "blend1");
        Add(FileKind.Model, "#F57C00", "stl", "3mf", "amf", "gcode", "step", "stp", "iges", "igs");
        Add(FileKind.Model, "#00838F", "obj", "fbx", "dae", "3ds", "max", "ma", "mb", "c4d", "lwo", "ply", "abc", "usd", "usda", "usdc", "usdz", "gltf", "glb");
        Add(FileKind.Model, "#C62828", "dwg", "dxf", "dwf", "skp", "rvt", "ifc", "sldprt", "sldasm", "f3d", "ipt", "iam");
        Add(FileKind.Model, "#222C37", "unity", "unitypackage", "prefab", "asset", "meta", "uasset", "umap", "pak", "tscn", "tres");

        // Programming languages
        Add(FileKind.Code, "#68217A", "cs", "csx");
        Add(FileKind.Code, "#512BD4", "csproj", "fsproj", "vbproj", "sln", "slnx", "props", "targets", "nuspec", "resx", "axaml", "xaml", "razor", "cshtml", "vbhtml", "aspx", "ascx", "asax", "ashx", "asmx", "config");
        Add(FileKind.Code, "#945DB7", "vb", "vbs", "bas", "frm", "cls");
        Add(FileKind.Code, "#378BBA", "fs", "fsi", "fsx", "fsscript");
        Add(FileKind.Code, "#5C6BC0", "c", "h");
        Add(FileKind.Code, "#00599C", "cpp", "cc", "cxx", "c++", "hpp", "hh", "hxx", "h++", "inl", "ipp", "tpp", "ixx", "cppm");
        Add(FileKind.Code, "#68217A", "vcxproj", "filters");
        Add(FileKind.Code, "#438EFF", "m");
        Add(FileKind.Code, "#6866FB", "mm");
        Add(FileKind.Code, "#F05138", "swift", "swiftinterface");
        Add(FileKind.Code, "#147EFB", "xcodeproj", "pbxproj", "xcworkspace", "xcconfig", "xcscheme", "storyboard", "xib", "entitlements", "xcstrings", "strings", "stringsdict", "xcassets", "plist");
        Add(FileKind.Code, "#B07219", "java", "class", "jsp", "jav");
        Add(FileKind.Code, "#7F52FF", "kt", "kts", "kotlin");
        Add(FileKind.Code, "#DC322F", "scala", "sc", "sbt");
        Add(FileKind.Code, "#4298B8", "groovy", "gvy", "gy", "gsh");
        Add(FileKind.Code, "#02303A", "gradle");
        Add(FileKind.Code, "#C71A36", "pom");
        Add(FileKind.Code, "#00ADD8", "go", "mod", "sum", "work");
        Add(FileKind.Code, "#DEA584", "rs", "rlib");
        Add(FileKind.Code, "#3776AB", "py", "pyw", "pyi", "pyx", "pxd", "pyc", "pyo", "pyd", "pth", "whl", "egg");
        Add(FileKind.Code, "#F37626", "ipynb");
        Add(FileKind.Code, "#CC342D", "rb", "erb", "rake", "gemspec", "ru", "rbs");
        Add(FileKind.Code, "#777BB4", "php", "phtml", "php3", "php4", "php5", "phps", "phar");
        Add(FileKind.Code, "#F7DF1E", "js", "mjs", "cjs", "es6", "es");
        Add(FileKind.Code, "#61DAFB", "jsx");
        Add(FileKind.Code, "#3178C6", "ts", "mts", "cts", "tsx", "tsbuildinfo");
        Add(FileKind.Code, "#41B883", "vue");
        Add(FileKind.Code, "#FF3E00", "svelte");
        Add(FileKind.Code, "#FF5D01", "astro");
        Add(FileKind.Code, "#2F2625", "coffee", "litcoffee");
        Add(FileKind.Code, "#E34F26", "html", "htm", "xhtml", "shtml", "mhtml", "mht", "hta");
        Add(FileKind.Code, "#1572B6", "css");
        Add(FileKind.Code, "#CC6699", "scss", "sass");
        Add(FileKind.Code, "#1D365D", "less");
        Add(FileKind.Code, "#FF6347", "styl", "stylus", "pcss", "postcss");
        Add(FileKind.Code, "#F0772B", "hbs", "handlebars", "mustache", "ejs", "pug", "jade", "njk", "liquid", "twig", "haml", "slim", "jinja", "jinja2", "j2", "tmpl", "tpl");
        Add(FileKind.Code, "#F9A825", "json", "jsonc", "json5", "jsonl", "ndjson", "geojson", "topojson", "har", "webmanifest", "map");
        Add(FileKind.Code, "#0060AC", "xml", "xsd", "xsl", "xslt", "dtd", "rss", "atom", "wsdl", "kml", "gpx", "rdf", "opf", "nfo", "manifest");
        Add(FileKind.Code, "#CB171E", "yaml", "yml");
        Add(FileKind.Code, "#9C4121", "toml");
        Add(FileKind.Code, "#6D8086", "ini", "cfg", "conf", "cnf", "properties", "prefs", "reg", "inf", "editorconfig", "gitconfig", "npmrc", "nvmrc", "htaccess", "nginx");
        Add(FileKind.Code, "#ECD53F", "env");
        Add(FileKind.Code, "#9E9E9E", "lock", "lockb");
        Add(FileKind.Code, "#F05032", "git", "gitignore", "gitattributes", "gitmodules", "gitkeep", "patch", "diff", "rej", "orig");
        Add(FileKind.Code, "#4EAA25", "sh", "bash", "zsh", "fish", "ksh", "csh", "tcsh", "command", "tool", "zshrc", "bashrc", "profile");
        Add(FileKind.Code, "#012456", "ps1", "psm1", "psd1", "ps1xml", "pssc");
        Add(FileKind.Code, "#C1F12E", "bat", "cmd", "btm");
        Add(FileKind.Code, "#E38C00", "sql", "psql", "pgsql", "mysql", "plsql", "ddl", "dml", "hql", "cql");
        Add(FileKind.Code, "#000080", "lua", "luau", "rockspec");
        Add(FileKind.Code, "#0175C2", "dart");
        Add(FileKind.Code, "#276DC3", "r", "rmd", "rproj", "rprofile");
        Add(FileKind.Code, "#39457E", "pl", "pm", "pod", "t", "psgi");
        Add(FileKind.Code, "#5E5086", "hs", "lhs", "cabal", "haskell");
        Add(FileKind.Code, "#6E4A7E", "ex", "exs", "eex", "heex", "leex");
        Add(FileKind.Code, "#B83998", "erl", "hrl", "escript", "app", "beam");
        Add(FileKind.Code, "#5881D8", "clj", "cljs", "cljc", "edn", "clojure");
        Add(FileKind.Code, "#60B5CC", "elm");
        Add(FileKind.Code, "#F7A41D", "zig", "zon");
        Add(FileKind.Code, "#FFE953", "nim", "nims", "nimble");
        Add(FileKind.Code, "#9558B2", "jl");
        Add(FileKind.Code, "#EF7A08", "ml", "mli", "mll", "mly", "re", "rei", "res", "resi");
        Add(FileKind.Code, "#4F5D95", "hx", "hxml");
        Add(FileKind.Code, "#A270BA", "cr");
        Add(FileKind.Code, "#B0CE4E", "d", "di");
        Add(FileKind.Code, "#4D41B1", "f", "f90", "f95", "f03", "f08", "for", "ftn");
        Add(FileKind.Code, "#E4CC98", "pas", "pp", "dpr", "dpk", "lpr");
        Add(FileKind.Code, "#02569B", "ada", "adb", "ads");
        Add(FileKind.Code, "#3F3F3F", "lisp", "lsp", "cl", "el", "scm", "ss", "rkt");
        Add(FileKind.Code, "#E16737", "pro", "prolog");
        Add(FileKind.Code, "#0F4C81", "cob", "cbl", "cpy");
        Add(FileKind.Code, "#5D6D7E", "v", "sv", "svh", "vh", "vhd", "vhdl");
        Add(FileKind.Code, "#6E4C13", "asm", "s", "nasm", "inc");
        Add(FileKind.Code, "#654FF0", "wasm", "wat", "wast");
        Add(FileKind.Code, "#363636", "sol", "vy", "move", "cairo");
        Add(FileKind.Code, "#E10098", "graphql", "gql", "graphqls");
        Add(FileKind.Code, "#4285F4", "proto", "thrift", "avsc", "capnp", "fbs");
        Add(FileKind.Code, "#2D3748", "prisma");
        Add(FileKind.Code, "#2496ED", "dockerfile", "containerfile", "dockerignore");
        Add(FileKind.Code, "#427819", "makefile", "mk", "mak", "ninja");
        Add(FileKind.Code, "#064F8C", "cmake");
        Add(FileKind.Code, "#7B42BC", "tf", "tfvars", "hcl", "nomad");
        Add(FileKind.Code, "#5277C3", "nix");
        Add(FileKind.Code, "#019733", "vim", "vimrc");
        Add(FileKind.Code, "#7F7F7F", "awk", "sed", "m4");
        Add(FileKind.Code, "#A8B9CC", "pch", "def", "idl", "odl");
        Add(FileKind.Code, "#5C2D91", "glsl", "vert", "frag", "geom", "comp", "hlsl", "fx", "shader", "metal", "wgsl", "cginc", "spv");
        Add(FileKind.Code, "#B7178C", "gd", "gdshader");
        Add(FileKind.Code, "#FF4088", "jinja", "dhall", "cue", "jsonnet", "libsonnet", "rego", "bicep", "pkl");
        Add(FileKind.Code, "#89E051", "ahk", "applescript", "scpt", "scptd", "jxa");
        Add(FileKind.Code, "#438EFF", "tcl", "tk", "exp");
        Add(FileKind.Code, "#1E88E5", "http", "rest", "postman_collection", "bru");
        Add(FileKind.Code, "#E8274B", "mojo", "gleam", "roc", "odin", "v", "jakt", "carbon", "vala", "vapi", "ceylon");

        return map;
    }
}
