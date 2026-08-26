// GPD Forge — MotionAssistant .ini importer tests (pure parser + thin file source). GPL-3.0-or-later.
using GpdForge.Import;
using Xunit;

namespace GpdForge.Core.Tests;

public class MotionAssistantImporterParseIniTests
{
    [Fact]
    public void Empty_or_whitespace_text_yields_no_profiles()
    {
        Assert.Empty(MotionAssistantImporter.ParseIni(""));
        Assert.Empty(MotionAssistantImporter.ParseIni("   \n\n  "));
    }

    [Fact]
    public void Single_section_with_canonical_keys_parses_exactly()
    {
        const string ini = "[Gaming]\nSTAPM=25\nFast=33\nSlow=28\nTctl=95\n";
        var p = Assert.Single(MotionAssistantImporter.ParseIni(ini));
        Assert.Equal("Gaming", p.Name);
        Assert.Equal(25, p.StapmW);
        Assert.Equal(33, p.FastW);
        Assert.Equal(28, p.SlowW);
        Assert.Equal(95, p.TctlC);
    }

    [Fact]
    public void Multiple_sections_parse_in_first_seen_order()
    {
        const string ini = "[Silent]\nSTAPM=8\n\n[Gaming]\nSTAPM=25\n\n[Balanced]\nSTAPM=15\n";
        var profiles = MotionAssistantImporter.ParseIni(ini);
        Assert.Equal(3, profiles.Count);
        Assert.Equal("Silent", profiles[0].Name);
        Assert.Equal("Gaming", profiles[1].Name);
        Assert.Equal("Balanced", profiles[2].Name);
    }

    [Fact]
    public void Missing_keys_fall_back_to_documented_defaults()
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni("[Custom]\nSTAPM=12\n"));
        Assert.Equal(12, p.StapmW);
        Assert.Equal(MotionAssistantImporter.DefaultFastW, p.FastW);
        Assert.Equal(MotionAssistantImporter.DefaultSlowW, p.SlowW);
        Assert.Equal(MotionAssistantImporter.DefaultTctlC, p.TctlC);
    }

    [Fact]
    public void Empty_section_body_still_yields_a_default_profile()
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni("[JustAName]\n"));
        Assert.Equal("JustAName", p.Name);
        Assert.Equal(MotionAssistantImporter.DefaultStapmW, p.StapmW);
    }

    [Theory]
    [InlineData("TDP")]
    [InlineData("stapm")]
    [InlineData("StapmW")]
    [InlineData("PL1")]
    [InlineData("sustained")]
    public void Stapm_aliases_all_resolve(string key)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\n{key}=18\n"));
        Assert.Equal(18, p.StapmW);
    }

    [Theory]
    [InlineData("FastLimit")]
    [InlineData("fast")]
    [InlineData("PL2")]
    [InlineData("boost")]
    [InlineData("turbo")]
    public void Fast_aliases_all_resolve(string key)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\n{key}=30\n"));
        Assert.Equal(30, p.FastW);
    }

    [Theory]
    [InlineData("SlowLimit")]
    [InlineData("slow")]
    [InlineData("PL3")]
    [InlineData("average")]
    public void Slow_aliases_all_resolve(string key)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\n{key}=22\n"));
        Assert.Equal(22, p.SlowW);
    }

    [Theory]
    [InlineData("TctlTemp")]
    [InlineData("tctl")]
    [InlineData("Temp")]
    [InlineData("thermal")]
    public void Tctl_aliases_all_resolve(string key)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\n{key}=88\n"));
        Assert.Equal(88, p.TctlC);
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni("[X]\nstapm=20\nFAST=25\nsLoW=22\nTCTL=90\n"));
        Assert.Equal(20, p.StapmW);
        Assert.Equal(25, p.FastW);
        Assert.Equal(22, p.SlowW);
        Assert.Equal(90, p.TctlC);
    }

    [Fact]
    public void Junk_lines_are_ignored_without_breaking_the_rest()
    {
        const string ini = "this is not a valid line at all\n[Gaming]\ngarbage without equals\nSTAPM=25\n???\nFast=33\n";
        var p = Assert.Single(MotionAssistantImporter.ParseIni(ini));
        Assert.Equal("Gaming", p.Name);
        Assert.Equal(25, p.StapmW);
        Assert.Equal(33, p.FastW);
    }

    [Fact]
    public void Keys_before_the_first_section_are_ignored()
    {
        const string ini = "STAPM=99\n[Gaming]\nSTAPM=25\n";
        var p = Assert.Single(MotionAssistantImporter.ParseIni(ini));
        Assert.Equal(25, p.StapmW); // the pre-section STAPM=99 never attached to a profile
    }

    [Fact]
    public void Full_line_and_trailing_comments_are_stripped()
    {
        const string ini = "; a whole comment line\n[Gaming] ; profile for games\n# another comment style\nSTAPM=25 ; sustained watts\n";
        var p = Assert.Single(MotionAssistantImporter.ParseIni(ini));
        Assert.Equal("Gaming", p.Name);
        Assert.Equal(25, p.StapmW);
    }

    [Theory]
    [InlineData("999", 40)] // above MaxStapmW -> clamped
    [InlineData("-5", 5)]   // below MinStapmW -> clamped
    [InlineData("40", 40)]  // exactly at the ceiling
    [InlineData("5", 5)]    // exactly at the floor
    public void Stapm_is_clamped_to_its_safe_band(string raw, int expected)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\nSTAPM={raw}\n"));
        Assert.Equal(expected, p.StapmW);
    }

    [Theory]
    [InlineData("30", 60)]  // below MinTctlC -> clamped up
    [InlineData("999", 95)] // above MaxTctlC -> clamped down
    public void Tctl_is_clamped_to_its_safe_band(string raw, int expected)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\nTctl={raw}\n"));
        Assert.Equal(expected, p.TctlC);
    }

    [Theory]
    [InlineData("25W", 25)]
    [InlineData("25 W", 25)]
    public void Watt_unit_suffixes_are_tolerated(string raw, int expected)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\nSTAPM={raw}\n"));
        Assert.Equal(expected, p.StapmW);
    }

    [Theory]
    [InlineData("92C", 92)]
    [InlineData("92°C", 92)]
    public void Temperature_unit_suffixes_are_tolerated(string raw, int expected)
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni($"[X]\nTctl={raw}\n"));
        Assert.Equal(expected, p.TctlC);
    }

    [Fact]
    public void Non_numeric_garbage_value_falls_back_to_default()
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni("[X]\nSTAPM=lots\n"));
        Assert.Equal(MotionAssistantImporter.DefaultStapmW, p.StapmW);
    }

    [Fact]
    public void Duplicate_key_in_a_section_uses_the_last_value()
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni("[X]\nSTAPM=10\nSTAPM=20\n"));
        Assert.Equal(20, p.StapmW);
    }

    [Fact]
    public void Section_name_whitespace_is_trimmed()
    {
        var p = Assert.Single(MotionAssistantImporter.ParseIni("[  Gaming  ]\nSTAPM=25\n"));
        Assert.Equal("Gaming", p.Name);
    }

    [Theory]
    [InlineData("[]\nSTAPM=25\n")]
    [InlineData("[Unterminated\nSTAPM=25\n")]
    public void Malformed_section_headers_are_skipped_as_junk(string ini)
    {
        Assert.Empty(MotionAssistantImporter.ParseIni(ini));
    }

    [Fact]
    public void Repeated_section_name_merges_into_one_profile()
    {
        const string ini = "[Gaming]\nSTAPM=25\n[Other]\nSTAPM=10\n[Gaming]\nFast=33\n";
        var profiles = MotionAssistantImporter.ParseIni(ini);
        Assert.Equal(2, profiles.Count);
        var gaming = profiles.First(p => p.Name == "Gaming");
        Assert.Equal(25, gaming.StapmW);
        Assert.Equal(33, gaming.FastW);
    }

    [Fact]
    public void A_realistic_multi_quirk_file_parses_end_to_end()
    {
        const string ini =
            "; GPD MotionAssistant export (sample)\n" +
            "[Silent]\n" +
            "TDP=8\n" +
            "FastLimit=12\n" +
            "SlowLimit=10\n" +
            "TctlTemp=85\n" +
            "\n" +
            "[Gaming]      ; main profile\n" +
            "STAPM = 25\n" +
            "Fast=33W\n" +
            "Slow=28\n" +
            "# tctl intentionally omitted -> default\n" +
            "some stray note without an equals sign\n";

        var profiles = MotionAssistantImporter.ParseIni(ini);
        Assert.Equal(2, profiles.Count);

        var silent = profiles[0];
        Assert.Equal("Silent", silent.Name);
        Assert.Equal(8, silent.StapmW);
        Assert.Equal(12, silent.FastW);
        Assert.Equal(10, silent.SlowW);
        Assert.Equal(85, silent.TctlC);

        var gaming = profiles[1];
        Assert.Equal("Gaming", gaming.Name);
        Assert.Equal(25, gaming.StapmW);
        Assert.Equal(33, gaming.FastW);
        Assert.Equal(28, gaming.SlowW);
        Assert.Equal(MotionAssistantImporter.DefaultTctlC, gaming.TctlC);
    }

    [Fact]
    public void ToTdpProfile_maps_fields_in_order()
    {
        var p = new ImportedProfile("X", 20, 30, 25, 90);
        var tdp = p.ToTdpProfile();
        Assert.Equal(20, tdp.StapmW);
        Assert.Equal(30, tdp.FastW);
        Assert.Equal(25, tdp.SlowW);
        Assert.Equal(90, tdp.TctlC);
    }
}

public class FileIniSourceTests
{
    [Fact]
    public void Missing_directory_reports_absent_and_returns_no_files()
    {
        string missing = Path.Combine(Path.GetTempPath(), "gpd-forge-tests-missing-" + Guid.NewGuid());
        var src = new FileIniSource(missing);
        Assert.False(src.DirectoryExists());
        Assert.Empty(src.ReadAllIniFiles());
    }

    [Fact]
    public void Reads_every_ini_file_in_the_directory_and_only_ini_files()
    {
        var dir = Directory.CreateTempSubdirectory("gpd-forge-ini-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.ini"), "[A]\nSTAPM=10\n");
            File.WriteAllText(Path.Combine(dir.FullName, "b.ini"), "[B]\nSTAPM=20\n");
            File.WriteAllText(Path.Combine(dir.FullName, "notes.txt"), "[C]\nSTAPM=30\n");

            var src = new FileIniSource(dir.FullName);
            Assert.True(src.DirectoryExists());
            var texts = src.ReadAllIniFiles();

            Assert.Equal(2, texts.Count);
            Assert.Contains(texts, t => t.Contains("[A]"));
            Assert.Contains(texts, t => t.Contains("[B]"));
            Assert.DoesNotContain(texts, t => t.Contains("[C]"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Default_profiles_directory_matches_the_documented_MotionAssistant_path()
    {
        Assert.Equal(@"C:\Program Files\Motion Assistant\Profiles", FileIniSource.DefaultProfilesDirectory);
        Assert.Equal(FileIniSource.DefaultProfilesDirectory, new FileIniSource().ProfilesDirectory);
    }
}
