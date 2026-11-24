using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Maui.ApplicationModel;

namespace AssetStudioMobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private const int FolderPickerRequestCode = 49231;
    private static TaskCompletionSource<Android.Net.Uri?>? _folderPickerCompletion;

    internal static Task<Android.Net.Uri?> PickFolderUriAsync()
    {
        var completion = new TaskCompletionSource<Android.Net.Uri?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _folderPickerCompletion, completion, null) != null)
            throw new InvalidOperationException("A folder picker is already open.");

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var activity = Platform.CurrentActivity
                    ?? throw new InvalidOperationException("Could not find the active Android activity.");
                var intent = new Intent(Intent.ActionOpenDocumentTree);
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                activity.StartActivityForResult(intent, FolderPickerRequestCode);
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _folderPickerCompletion, null)?.TrySetException(exception);
            }
        });

        return completion.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != FolderPickerRequestCode)
            return;

        var completion = Interlocked.Exchange(ref _folderPickerCompletion, null);
        if (completion == null)
            return;

        completion.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}
