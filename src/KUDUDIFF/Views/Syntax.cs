using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Kududiff.Views
{
    /// <summary>
    /// Syntax colouring by file type: the languages the editor component brings, plus YAML, INI, batch and Bash files.
    /// The colours are made lighter for the dark theme, so code stays readable on a dark background.
    /// </summary>
    public static class Syntax
    {
        private const string Builtin = "ICSharpCode.AvalonEdit.Highlighting.Resources.";
        private const string Own = "Kududiff.Highlighting.";

        // Language name -> where its definition is.
        private static readonly Dictionary<string, string> Resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C#"] = Builtin + "CSharp-Mode.xshd",
            ["C++"] = Builtin + "CPP-Mode.xshd",
            ["CSS"] = Builtin + "CSS-Mode.xshd",
            ["HTML"] = Builtin + "HTML-Mode.xshd",
            ["ASP/XHTML"] = Builtin + "ASPX.xshd",
            ["Java"] = Builtin + "Java-Mode.xshd",
            ["JavaScript"] = Builtin + "JavaScript-Mode.xshd",
            ["Json"] = Builtin + "Json.xshd",
            ["MarkDown"] = Builtin + "MarkDown-Mode.xshd",
            ["PHP"] = Builtin + "PHP-Mode.xshd",
            ["Patch"] = Builtin + "Patch-Mode.xshd",
            ["PowerShell"] = Builtin + "PowerShell.xshd",
            ["Python"] = Builtin + "Python-Mode.xshd",
            ["TSQL"] = Builtin + "TSQL-Mode.xshd",
            ["VB"] = Builtin + "VB-Mode.xshd",
            ["XML"] = Builtin + "XML-Mode.xshd",
            ["YAML"] = Own + "YAML.xshd",
            ["INI"] = Own + "INI.xshd",
            ["Batch"] = Own + "Batch.xshd",
            ["Bash"] = Own + "Bash.xshd"
        };

        private static readonly Dictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = "C#", [".csx"] = "C#",
            [".c"] = "C++", [".h"] = "C++", [".cc"] = "C++", [".cpp"] = "C++", [".cxx"] = "C++", [".hpp"] = "C++", [".hxx"] = "C++", [".ino"] = "C++",
            [".css"] = "CSS", [".scss"] = "CSS", [".less"] = "CSS",
            [".htm"] = "HTML", [".html"] = "HTML", [".xhtml"] = "HTML", [".vue"] = "HTML", [".svelte"] = "HTML",
            [".asp"] = "ASP/XHTML", [".aspx"] = "ASP/XHTML", [".cshtml"] = "ASP/XHTML", [".razor"] = "ASP/XHTML", [".master"] = "ASP/XHTML",
            [".java"] = "Java", [".kt"] = "Java", [".kts"] = "Java", [".gradle"] = "Java", [".scala"] = "Java", [".groovy"] = "Java",
            [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript", [".jsx"] = "JavaScript",
            [".ts"] = "JavaScript", [".tsx"] = "JavaScript", [".mts"] = "JavaScript", [".cts"] = "JavaScript",
            [".json"] = "Json", [".jsonc"] = "Json", [".json5"] = "Json", [".geojson"] = "Json", [".webmanifest"] = "Json",
            [".md"] = "MarkDown", [".markdown"] = "MarkDown",
            [".php"] = "PHP", [".phtml"] = "PHP",
            [".patch"] = "Patch", [".diff"] = "Patch",
            [".ps1"] = "PowerShell", [".psm1"] = "PowerShell", [".psd1"] = "PowerShell",
            [".py"] = "Python", [".pyw"] = "Python", [".pyi"] = "Python",
            [".sql"] = "TSQL",
            [".vb"] = "VB", [".vbs"] = "VB", [".bas"] = "VB",
            [".xml"] = "XML", [".xaml"] = "XML", [".xsd"] = "XML", [".xsl"] = "XML", [".xslt"] = "XML", [".config"] = "XML", [".manifest"] = "XML",
            [".csproj"] = "XML", [".vbproj"] = "XML", [".vcxproj"] = "XML", [".props"] = "XML", [".targets"] = "XML", [".nuspec"] = "XML",
            [".resx"] = "XML", [".svg"] = "XML", [".plist"] = "XML", [".xshd"] = "XML", [".wxs"] = "XML",
            [".yml"] = "YAML", [".yaml"] = "YAML",
            [".ini"] = "INI", [".cfg"] = "INI", [".conf"] = "INI", [".properties"] = "INI", [".inf"] = "INI", [".reg"] = "INI",
            [".editorconfig"] = "INI", [".gitconfig"] = "INI", [".toml"] = "INI", [".iss"] = "INI", [".env"] = "INI",
            [".bat"] = "Batch", [".cmd"] = "Batch",
            [".sh"] = "Bash", [".bash"] = "Bash", [".zsh"] = "Bash"
        };

        public static IEnumerable<string> Languages => Resources.Keys.OrderBy(k => k);

        /// <summary>The language for a file name, or null for plain text.</summary>
        public static string LanguageFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var name = Path.GetFileName(path);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".bashrc", StringComparison.OrdinalIgnoreCase)) return "Bash";
            if (name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)) return "INI";
            return ByExtension.TryGetValue(Path.GetExtension(path), out var language) ? language : null;
        }

        private static readonly Dictionary<string, IHighlightingDefinition> Light = new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IHighlightingDefinition> Dark = new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The colouring for a language and theme (cached), or null.</summary>
        public static IHighlightingDefinition Get(string language, bool dark)
        {
            if (language == null || !Resources.ContainsKey(language)) return null;
            var cache = dark ? Dark : Light;
            if (cache.TryGetValue(language, out var found)) return found;
            try
            {
                var xml = ReadResource(Resources[language]);
                if (xml == null) return null;
                if (dark) xml = Lighten(xml);
                using (var reader = XmlReader.Create(new StringReader(xml)))
                {
                    var definition = HighlightingLoader.Load(HighlightingLoader.LoadXshd(reader), new Resolver(dark));
                    cache[language] = definition;
                    return definition;
                }
            }
            catch (Exception ex) when (ex is XmlException || ex is HighlightingDefinitionInvalidException || ex is IOException)
            {
                cache[language] = null;
                return null;
            }
        }

        private static string ReadResource(string name)
        {
            var assembly = name.StartsWith(Own) ? Assembly.GetExecutingAssembly() : typeof(HighlightingManager).Assembly;
            using (var stream = assembly.GetManifestResourceStream(name))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        /// <summary>Makes every text colour light enough to read on the dark editor, and drops background colours.</summary>
        private static string Lighten(string xml)
        {
            xml = Regex.Replace(xml, "foreground=\"([^\"]+)\"", m =>
            {
                try
                {
                    var c = (Color)ColorConverter.ConvertFromString(m.Groups[1].Value);
                    return $"foreground=\"#{Up(c.R):X2}{Up(c.G):X2}{Up(c.B):X2}\"";
                }
                catch (FormatException)
                {
                    return m.Value;
                }
            });
            return Regex.Replace(xml, "\\s+background=\"[^\"]+\"", string.Empty);
        }

        private static byte Up(byte v) => (byte)(v + (255 - v) * 0.55);

        /// <summary>Definitions that use another language (HTML uses JavaScript and CSS) get it in the same theme.</summary>
        private sealed class Resolver : IHighlightingDefinitionReferenceResolver
        {
            private readonly bool _dark;

            public Resolver(bool dark) => _dark = dark;

            public IHighlightingDefinition GetDefinition(string name) => Get(name, _dark) ?? HighlightingManager.Instance.GetDefinition(name);
        }
    }
}
