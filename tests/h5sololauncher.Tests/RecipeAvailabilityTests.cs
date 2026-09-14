using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Runtime;
using Xunit;

namespace H5SoloLauncher.Tests;

public sealed class RecipeAvailabilityTests
{
    [Theory]
    [InlineData("completion-ui-recipe.json", "COMPLETION_UI_UNSUPPORTED")]
    [InlineData("display-ui-recipe.json", "DISPLAY_UI_UNSUPPORTED")]
    public void SourceOnlyBuildExplainsMissingPreparationRecipes(string name, string unsupported)
    {
        using var resource = typeof(CompletionConfiguration).Assembly.GetManifestResourceStream("H5SoloLauncher.Core.Runtime." + name);
        var error = Assert.Throws<CacheException>(() =>
        {
            if (name.StartsWith("completion")) CompletionConfiguration.PatchUi([]);
            else DisplayConfiguration.PatchUi([]);
        });
        Assert.Equal(resource is null ? "UI_RECIPE_NOT_INCLUDED" : unsupported, error.Code);
        if (resource is null) Assert.Contains("complete cache", error.Message);
    }
}
