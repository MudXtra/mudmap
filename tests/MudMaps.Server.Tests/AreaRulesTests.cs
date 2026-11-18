using MudMaps.Server.Maps;

namespace MudMaps.Server.Tests;

// Golden cases taken from tools/build_data.py so the C# port draws the same areas.
public class AreaRulesTests
{
    [Theory]
    [InlineData("src/MudBlazor/Components/DataGrid/MudDataGrid.razor.cs", "DataGrid")]
    [InlineData("src/MudBlazor.UnitTests/Components/DataGridTests.cs", "DataGrid")]
    [InlineData("src/MudBlazor.Docs/Pages/Components/Chart/Examples/BarExample.razor", "Chart")]
    [InlineData("src/MudBlazor/Services/Browser/BrowserService.cs", "Services")]
    [InlineData("src/MudBlazor.Analyzers/Foo/Bar.cs", "Analyzers")]
    [InlineData("MudBlazor.Extensions/Components/MudExFileDisplay.razor", "FileDisplay")]
    [InlineData("src/MudBlazor/TScripts/mudElementReference.ts", "TScripts")]
    [InlineData("src/MudX/Components/MudXCodeBlock/CodeFile.cs", "CodeBlock")]
    [InlineData("Program.cs", "Core")]
    [InlineData("src/MudBlazor.UnitTests.Viewer/TestComponents/Charts/ChartTest.razor", "Chart")]
    [InlineData("src/CodeBeam.MudBlazor.Extensions/Components/ComboBox/MudComboBox.razor", "ComboBox")]
    [InlineData("src/MudBlazor/Base/MudBaseInput.cs", "Base")]
    public void AreaOf_maps_paths_to_folder_areas(string path, string expected) =>
        Assert.Equal(expected, AreaRules.AreaOf(path));

    [Theory]
    [InlineData("src/MudBlazor/wwwroot/lib/x.min.js")]
    [InlineData("src/MudBlazor/Translations/de.yml")]
    [InlineData("README.md")]
    [InlineData("")]
    [InlineData(null)]
    public void AreaOf_drops_noise(string? path) => Assert.Null(AreaRules.AreaOf(path));

    [Theory]
    [InlineData("Docs", "Docs & samples")]
    [InlineData("DataGrid", "Data Grid")]
    [InlineData("TScripts", "TypeScript")]
    [InlineData("Unit", "Unit tests")]
    [InlineData("SelectExtended", "Select Extended")]
    public void Label_is_readable(string key, string expected) => Assert.Equal(expected, AreaRules.Label(key));

    [Theory]
    [InlineData("src/MudBlazor.UnitTests/Components/ButtonTests.cs", true)]
    [InlineData("tests/Foo/Bar.cs", true)]
    [InlineData("src/Heron.MudCalendar.Tests/CalendarTest.cs", true)]
    [InlineData("src/MudX/Components/Sheet/MudXSheetTests.razor", true)]
    [InlineData("src/app/foo.spec.ts", true)]
    [InlineData("src/MudBlazor/Components/Button/MudButton.razor.cs", false)]
    [InlineData("src/MudBlazor/Components/TestComponentBase.cs", false)]
    [InlineData("src/Latest/Contest.cs", false)]
    public void Test_code_is_recognised(string path, bool expected) => Assert.Equal(expected, AreaRules.IsTest(path));
}
