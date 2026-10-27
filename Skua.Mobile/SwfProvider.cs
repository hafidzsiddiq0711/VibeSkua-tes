using Android.Content;
using Android.Database;
using Android.OS;
using Android.Runtime;

namespace Skua.Mobile;

/// <summary>Serves files/skua-mobile.swf to the Artix player (read-only, via a granted URI).</summary>
[ContentProvider(new[] { Authority }, Name = "com.skua.mobile.SwfProvider", Exported = false, GrantUriPermissions = true)]
public class SwfProvider : ContentProvider
{
    public const string Authority = "com.skua.mobile.swf";

    public override bool OnCreate() => true;

    public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
    {
        string name = Path.GetFileName(uri.Path ?? AppFiles.SwfFileName);
        Java.IO.File file = new(Context!.FilesDir, name == AppFiles.SwfFileName ? name : AppFiles.SwfFileName);
        return ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
    }

    public override string? GetType(Android.Net.Uri uri) => "application/x-shockwave-flash";

    public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;

    public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) => null;

    public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;

    public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;
}
