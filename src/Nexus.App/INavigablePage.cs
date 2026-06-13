namespace Nexus.App;

/// <summary>
/// Page implements this to receive the selected NavItem.Tag when navigated to.
/// </summary>
public interface INavigablePage
{
    void OnNavigatedTo(string navTag);
}
