using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Runtime;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class RecipeAvailabilityTests
{
    [Theory]
    [InlineData("completion-ui-recipe.json", "COMPLETION_UI_UNSUPPORTED")]
    [InlineData("display-ui-recipe.json", "DISPLAY_UI_UNSUPPORTED")]
    public void BuildIncludesPreparationRecipesAndRejectsUnsupportedSource(string name, string unsupported)
    {
        using var resource = typeof(CompletionConfiguration).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Runtime." + name);
        Assert.NotNull(resource);
        var error = Assert.Throws<CacheException>(() =>
        {
            if (name.StartsWith("completion")) CompletionConfiguration.PatchUi([]);
            else DisplayConfiguration.PatchUi([]);
        });
        Assert.Equal(unsupported, error.Code);
    }
}
