using AryanEbookLibrary.Services.Metadata;

namespace AryanEbookLibrary.Tests;

/// <summary>
/// Burmese written in Zawgyi, from the library's own ground truth: titles whose Zawgyi text sits beside a file name
/// in Unicode.
/// </summary>
public class ZawgyiTests
{
    [Theory]
    [InlineData("ဗန္းေမာ္တင္ေအာင္", "ဗန်းမော်တင်အောင်")]
    [InlineData("အညၾတ", "အညတြ")]
    [InlineData("မင္းခိုက္စိုးစန္", "မင်းခိုက်စိုးစန်")]
    public void Zawgyi_reads_as_unicode(string zawgyi, string unicode) => Assert.Equal(unicode, Zawgyi.Fix(zawgyi));

    [Theory]
    [InlineData("ဗန်းမော်တင်အောင်")]
    [InlineData("အညတြ")]
    [InlineData("မင်းခိုက်စိုးစန်")]
    [InlineData("Becoming the Hacker")]
    public void Unicode_and_english_are_left_alone(string text) => Assert.Equal(text, Zawgyi.Fix(text));
}
