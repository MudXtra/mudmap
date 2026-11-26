using Microsoft.JSInterop;
using MudMaps.Shared;

namespace MudMaps.Server.Tests;

// "Skip animations" is off unless the reader ticked it, and the choice is kept in local storage.
public class MotionSettingsTests
{
    private sealed class FakeStorage : IJSRuntime
    {
        public Dictionary<string, string> Items { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            var key = (string)args![0]!;
            switch (identifier)
            {
                case "localStorage.getItem":
                    return ValueTask.FromResult((TValue)(object?)Items.GetValueOrDefault(key)!);
                case "localStorage.setItem":
                    Items[key] = (string)args[1]!;
                    return ValueTask.FromResult(default(TValue)!);
                default:
                    throw new InvalidOperationException(identifier);
            }
        }
    }

    [Fact]
    public async Task Animations_play_unless_the_reader_chose_to_skip_them()
    {
        var motion = new MotionSettings(new FakeStorage());
        await motion.LoadAsync();
        Assert.False(motion.SkipAnimations);
    }

    [Fact]
    public async Task The_choice_is_saved_and_read_back()
    {
        var storage = new FakeStorage();
        var changed = 0;
        var motion = new MotionSettings(storage);
        motion.Changed += () => changed++;
        await motion.SetAsync(true);
        Assert.Equal("true", storage.Items[MotionSettings.StorageKey]);
        Assert.Equal(1, changed);

        var later = new MotionSettings(storage);
        await later.LoadAsync();
        Assert.True(later.SkipAnimations);
    }

    [Fact]
    public void The_shared_script_url_carries_a_content_version()
    {
        Assert.Matches(@"^\./_content/MudMaps\.Shared/zoom\.js\?v=-?\d+$", SharedAssets.ZoomModule);
        Assert.DoesNotMatch(@"\?v=0$", SharedAssets.ZoomModule);
    }
}
