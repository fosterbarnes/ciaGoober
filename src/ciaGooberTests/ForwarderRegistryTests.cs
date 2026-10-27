using System.Text.Json.Nodes;
using CiaGoober.Core;

namespace CiaGoober.Tests;

public sealed class ForwarderRegistryTests : IDisposable
{
    // Python json.dumps(indent=2, ensure_ascii=False) layout with fields the app does not use.
    private const string _REGISTRY = """
        {
          "sdCiaDirectory": "/.cia",
          "nextUniqueId": "0xFF402",
          "games": [
            {
              "name": "pokémon",
              "rom": "sd:/roms/nds/Pokémon Diamond.nds",
              "uniqueId": "0xFF400",
              "banner": "pokemon.png",
              "icon": null
            },
            {
              "name": "zelda",
              "system": "gba",
              "rom": "Zelda.gba",
              "uniqueId": "0xFF401",
              "banner": null,
              "saveType": "flash1m",
              "extra": {
                "keep": [
                  1,
                  2.5
                ]
              }
            }
          ]
        }

        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("ciaGooberTests").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    [Fact]
    public async Task KnownRomKeepsItsIdAndLeavesTheFileUnchanged()
    {
        var path = Write(_REGISTRY);
        var registry = ForwarderRegistry.Open(path);
        var entry = await registry.ReserveAsync(GameSystem.Nds, "SD:/ROMS/nds/POKÉMON DIAMOND.NDS", "x");
        Assert.Equal(
            new RegistryEntry("pokémon", GameSystem.Nds, "sd:/roms/nds/Pokémon Diamond.nds", 0xFF400), entry);
        var gba = await registry.ReserveAsync(GameSystem.Gba, @"D:\gba\zelda.GBA", "x");
        Assert.Equal(0xFF401, gba.UniqueId);
        Assert.Equal(_REGISTRY, File.ReadAllText(path));
    }

    [Fact]
    public async Task NewRomAppendsEntryAndKeepsEverythingElse()
    {
        var path = Write(_REGISTRY);
        var registry = ForwarderRegistry.Open(path);
        var entry = await registry.ReserveAsync(GameSystem.Gba, @"D:\gba\Métroid.gba", "métroid");
        Assert.Equal(new RegistryEntry("métroid", GameSystem.Gba, "Métroid.gba", 0xFF402), entry);
        var expected = _REGISTRY.Replace("\"0xFF402\"", "\"0xFF403\"").Replace("""
                }
              ]
            }
            """, """
                },
                {
                  "name": "métroid",
                  "system": "gba",
                  "rom": "Métroid.gba",
                  "uniqueId": "0xFF402",
                  "banner": null
                }
              ]
            }
            """);
        Assert.Equal(expected, File.ReadAllText(path));
    }

    // A GB or NES entry must reload as its system: an unknown system fails the whole registry, and
    // reading it as another system would hand the same file name a second ID.
    [Theory]
    [InlineData(GameSystem.Gb, @"D:\gb\Zelda.gbc", "Zelda.gbc", "zelda.GBC")]
    [InlineData(GameSystem.Nes, @"D:\nes\Zelda.nes", "Zelda.nes", "zelda.NES")]
    public async Task EmbeddedEntryReloadsAsItsOwnSystem(GameSystem system, string romPath,
        string key, string otherCase)
    {
        var path = Write(_REGISTRY);
        var entry = await ForwarderRegistry.Open(path).ReserveAsync(system, romPath, "z");
        var reopened = ForwarderRegistry.Open(path);
        Assert.Equal(new RegistryEntry("z", system, key, 0xFF402), entry);
        Assert.Equal(entry, reopened.Find(system, otherCase));
    }

    [Theory]
    [InlineData("0xFF410", "0xFF400", 0xFF410)]
    [InlineData("0xFF400", "0xFF405", 0xFF406)]
    public async Task NewIdNeverReusesOrLowers(string next, string existing, int expected)
    {
        var path = Write(Registry(next, Game("sd:/a.nds", existing)));
        var entry = await ForwarderRegistry.Open(path).ReserveAsync(GameSystem.Nds, "/b.nds", "b");
        Assert.Equal(expected, entry.UniqueId);
        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal($"0x{expected + 1:X}", saved["nextUniqueId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("sd:/a.nds", "0xFF400", "SD:/A.NDS", "0xFF401")]
    [InlineData("sd:/a.nds", "0xFF400", "sd:/b.nds", "0xFF400")]
    [InlineData("sd:/a.nds", "0xFF3FF", "sd:/b.nds", "0xFF401")]
    [InlineData("sd:/a.nds", "0xFF400", "sd:/b.txt", "0xFF401")]
    public void OpenRejectsDuplicateOrInvalidGames(string rom1, string id1, string rom2, string id2)
    {
        var path = Write(Registry("0xFF402", Game(rom1, id1), Game(rom2, id2)));
        Assert.Throws<InvalidDataException>(() => ForwarderRegistry.Open(path));
    }

    [Fact]
    public async Task ParallelCallersOnTwoInstancesGetDistinctIds()
    {
        var path = Write(Registry("0xFF400"));
        ForwarderRegistry[] registries = [ForwarderRegistry.Open(path), ForwarderRegistry.Open(path)];
        var entries = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            registries[i % 2].ReserveAsync(GameSystem.Gba, $"game{i}.gba", $"game{i}"))));
        Assert.Equal(Enumerable.Range(0xFF400, 8), entries.Select(e => e.UniqueId).Order());
        var reopened = ForwarderRegistry.Open(path).Entries();
        Assert.Equal(entries.OrderBy(e => e.UniqueId), reopened.OrderBy(e => e.UniqueId));
    }

    // A new install has no app data folder yet.
    [Fact]
    public async Task MissingRegistryStartsAtFirstIdAndIsWrittenOnFirstReserve()
    {
        var path = Path.Combine(_folder, "appData", "forwarders.json");
        var registry = ForwarderRegistry.Open(path);
        Assert.Empty(registry.Entries());
        Assert.False(File.Exists(path));
        var entry = await registry.ReserveAsync(GameSystem.Gba, "game.gba", "game");
        Assert.Equal(0xFF400, entry.UniqueId);
        Assert.Equal([entry], ForwarderRegistry.Open(path).Entries());
    }

    [Fact]
    public async Task RefusesPastTheLastId()
    {
        var path = Write(Registry("0xFF7FF", Game("sd:/a.nds", "0xFF7FE")));
        var registry = ForwarderRegistry.Open(path);
        Assert.Equal(0xFF7FF, (await registry.ReserveAsync(GameSystem.Nds, "sd:/b.nds", "b")).UniqueId);
        var full = File.ReadAllText(path);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ReserveAsync(GameSystem.Nds, "sd:/c.nds", "c"));
        Assert.Equal(full, File.ReadAllText(path));
    }

    private static string Game(string rom, string id)
    {
        var system = rom.EndsWith(".gba") ? "\"system\": \"gba\", " : "";
        return $$"""{"name": "g", {{system}}"rom": "{{rom}}", "uniqueId": "{{id}}"}""";
    }

    private static string Registry(string next, params string[] games) =>
        $$"""{"nextUniqueId": "{{next}}", "games": [{{string.Join(", ", games)}}]}""";

    private string Write(string text)
    {
        var path = Path.Combine(_folder, "forwarders.json");
        File.WriteAllText(path, text);
        return path;
    }
}
