using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using VSL.Editor;
using VSL.Lsp;

namespace VSL.Tests;

public class OutliningTests
{
    const string Code = """
        using System;
        using System.IO;

        namespace Demo
        {
            /// <summary>
            /// A greeter.
            /// </summary>
            public class Greeter
            {
                #region Fields
                private string _name = "{not a brace}";
                #endregion

                public Greeter(string name)
                {
                    _name = name;
                }

                public string Greet()
                {
                    if (_name.Length > 0)
                    {
                        return $"Hi {_name}";
                    }
                    return "Hi";
                }
            }
        }
        """;

    static string Shown(TextDocument document, NewFolding folding)
    {
        // The line a block hangs off: where it starts.
        var line = document.GetLineByOffset(folding.StartOffset);
        return document.GetText(line).Trim();
    }

    [AvaloniaFact]
    public void Scans_csharp_blocks_regions_and_doc_comments()
    {
        var document = new TextDocument(Code);
        var foldings = Outlining.Scan(document, "/x/Greeter.cs");
        var summary = foldings.Select(f => (Shown(document, f), f.Name, f.IsDefinition)).ToList();
        Assert.Equal(
        [
            ("namespace Demo", "{...}", false),
            ("/// <summary>", "/// <summary> ...", false),
            ("public class Greeter", "{...}", false),
            ("#region Fields", "Fields", false),
            ("public Greeter(string name)", "{...}", true),
            ("public string Greet()", "{...}", true),
            ("if (_name.Length > 0)", "{...}", false),
        ], summary);
        // Allman braces fold from the end of the declaration, so "public string Greet() {...}" stays one line.
        var greet = foldings.Single(f => Shown(document, f) == "public string Greet()");
        Assert.Equal(document.GetLineByOffset(greet.StartOffset).EndOffset, greet.StartOffset);
        Assert.Equal('}', document.GetCharAt(greet.EndOffset - 1));
    }

    [AvaloniaFact]
    public void Scans_json_and_xml()
    {
        var json = new TextDocument("{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": \"}\"\n}\n");
        Assert.Equal(["{...}", "[...]"], Outlining.Scan(json, "/x/appsettings.json").Select(f => f.Name));
        var xml = new TextDocument("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <A>1</A>\n  </PropertyGroup>\n</Project>\n");
        Assert.Equal(2, Outlining.Scan(xml, "/x/App.csproj").Count);
    }

    [AvaloniaFact]
    public void Maps_roslyn_folding_ranges()
    {
        var document = new TextDocument(Code);
        int Line(string text) => document.Lines.First(l => document.GetText(l).Trim() == text).LineNumber - 1;
        var ranges = new List<LspFoldingRange>
        {
            new(0, 6, 1, 16, "imports", null),
            new(Line("public class Greeter"), null, document.LineCount - 2, null, null, null),
            new(Line("#region Fields"), null, Line("#endregion"), null, "region", null),
            new(Line("public string Greet()"), null, Line("return \"Hi\";") + 1, null, null, null),
        };
        var foldings = Outlining.FromLsp(document, ranges);
        Assert.Equal("Fields", foldings.Single(f => Shown(document, f) == "#region Fields").Name);
        var greet = foldings.Single(f => Shown(document, f) == "public string Greet()");
        Assert.True(greet.IsDefinition);
        Assert.Equal(document.GetLineByOffset(greet.StartOffset).EndOffset, greet.StartOffset);
        Assert.False(foldings.Single(f => Shown(document, f) == "using System;").IsDefinition);
    }
}
