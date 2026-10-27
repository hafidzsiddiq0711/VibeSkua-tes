using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Skua.Host;

namespace Skua.Mobile;

/// <summary>Small helpers for the code-built UI (black and gold, like the Skua theme).</summary>
public static class Ui
{
    public static readonly Color Background = Color.ParseColor("#101014");
    public static readonly Color Card = Color.ParseColor("#1B1B22");
    public static readonly Color CardBorder = Color.ParseColor("#2C2C36");
    public static readonly Color Gold = Color.ParseColor("#FDAF2D");
    public static readonly Color Text = Color.ParseColor("#F2F2F2");
    public static readonly Color Muted = Color.ParseColor("#9A9AA6");
    public static readonly Color Info = Color.ParseColor("#8CD5FF");
    public static readonly Color Good = Color.ParseColor("#7CDB8A");
    public static readonly Color Bad = Color.ParseColor("#FF8A80");

    public static int Dp(Context c, float dp) => (int)(dp * c.Resources!.DisplayMetrics!.Density + 0.5f);

    public static TextView Label(Context c, string text, float sp = 14, Color? color = null, bool bold = false)
    {
        TextView t = new(c) { Text = text };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color ?? Text);
        if (bold)
            t.SetTypeface(null, TypefaceStyle.Bold);
        return t;
    }

    public static GradientDrawable Rounded(Color fill, float radiusDp, Context c, Color? stroke = null)
    {
        GradientDrawable d = new();
        d.SetColor(fill);
        d.SetCornerRadius(Dp(c, radiusDp));
        if (stroke is Color s)
            d.SetStroke(Dp(c, 1), s);
        return d;
    }

    public static Android.Widget.Button Button(Context c, string text, Action onClick, bool primary = false, float sp = 13)
    {
        Android.Widget.Button b = new(c) { Text = text };
        b.SetAllCaps(false);
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        b.Background = Rounded(primary ? Gold : Color.ParseColor("#2A2A33"), 10, c);
        b.SetTextColor(primary ? Color.Black : Text);
        b.SetPadding(Dp(c, 8), Dp(c, 6), Dp(c, 8), Dp(c, 6));
        b.SetMinHeight(Dp(c, 40));
        b.SetMinimumHeight(Dp(c, 40));
        b.Click += (_, _) => onClick();
        return b;
    }

    public static LinearLayout Row(Context c, params View[] views)
    {
        LinearLayout row = new(c) { Orientation = Orientation.Horizontal };
        foreach (View v in views)
        {
            LinearLayout.LayoutParams lp = new(0, ViewGroup.LayoutParams.WrapContent, 1f);
            lp.SetMargins(Dp(c, 3), Dp(c, 3), Dp(c, 3), Dp(c, 3));
            row.AddView(v, lp);
        }
        return row;
    }

    public static EditText Field(Context c, string hint, string value, InputTypes type = InputTypes.ClassText)
    {
        EditText e = new(c) { Hint = hint, Text = value, InputType = type };
        e.SetSingleLine(true);
        e.SetTextColor(Text);
        e.SetHintTextColor(Muted);
        return e;
    }

    public static AlertDialog.Builder Dialog(Context c, string title) =>
        new AlertDialog.Builder(c, Android.Resource.Style.ThemeDeviceDefaultDialogAlert)!.SetTitle(title)!;

    public static void Toast(Context c, string text) => Android.Widget.Toast.MakeText(c, text, ToastLength.Long)!.Show();

    /// <summary>
    /// Settings dialog: one row per option (switch, number, text or list), grouped, searchable.
    /// <paramref name="onSave"/> gets every value as text.
    /// </summary>
    public static void ShowOptions(Activity activity, string title, IReadOnlyList<OptionDef> defs,
        IReadOnlyDictionary<string, string> values, Action<Dictionary<string, string>> onSave, string? note = null)
    {
        Context c = activity;
        LinearLayout box = new(c) { Orientation = Orientation.Vertical };
        box.SetPadding(Dp(c, 18), Dp(c, 8), Dp(c, 18), Dp(c, 8));
        if (note is not null)
        {
            TextView n = Label(c, note, 12, Muted);
            n.SetPadding(0, 0, 0, Dp(c, 8));
            box.AddView(n);
        }

        EditText? search = null;
        if (defs.Count > 12)
        {
            search = Field(c, "Cari opsi …", "");
            box.AddView(search);
        }

        List<(OptionDef Def, View Row, Func<string> Read)> rows = new();
        Dictionary<string, TextView> headers = new();
        string? group = null;
        foreach (OptionDef def in defs)
        {
            if (def.Group != group)
            {
                group = def.Group;
                if (group.Length > 0)
                {
                    TextView h = Label(c, group, 13, Gold, bold: true);
                    h.SetPadding(0, Dp(c, 12), 0, Dp(c, 2));
                    box.AddView(h);
                    headers[group] = h;
                }
            }

            string current = values.TryGetValue(def.Key, out string? v) ? v : def.Default;
            LinearLayout row = new(c) { Orientation = Orientation.Vertical };
            row.SetPadding(0, Dp(c, 4), 0, Dp(c, 4));
            Func<string> read;
            switch (def.Type)
            {
                case "bool":
                {
                    CheckBox check = new(c) { Text = def.Label, Checked = bool.TryParse(current, out bool b) && b };
                    check.SetTextColor(Text);
                    row.AddView(check);
                    // Keep the CoreBots style ("True"/"False") when the stored value used it.
                    bool pascal = current.Length > 0 && char.IsUpper(current[0]);
                    read = () => pascal ? (check.Checked ? "True" : "False") : (check.Checked ? "true" : "false");
                    break;
                }
                case "choice":
                {
                    row.AddView(Label(c, def.Label, 13, Muted));
                    string[] choices = def.Choices ?? Array.Empty<string>();
                    Spinner spinner = new(c);
                    ArrayAdapter<string> adapter = new(c, Android.Resource.Layout.SimpleSpinnerDropDownItem, choices);
                    spinner.Adapter = adapter;
                    int index = Array.FindIndex(choices, x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                        spinner.SetSelection(index);
                    row.AddView(spinner);
                    read = () => spinner.SelectedItemPosition >= 0 && spinner.SelectedItemPosition < choices.Length ? choices[spinner.SelectedItemPosition] : current;
                    break;
                }
                default:
                {
                    row.AddView(Label(c, def.Label, 13, Muted));
                    EditText edit = Field(c, def.Default, current,
                        def.Type == "int" ? InputTypes.ClassNumber | InputTypes.NumberFlagSigned : InputTypes.ClassText);
                    row.AddView(edit);
                    read = () => edit.Text ?? "";
                    break;
                }
            }
            if (def.Description.Length > 0)
                row.AddView(Label(c, def.Description, 11, Muted));
            box.AddView(row);
            rows.Add((def, row, read));
        }

        if (search is not null)
        {
            search.TextChanged += (_, _) =>
            {
                string q = search.Text ?? "";
                foreach (var (def, row, _) in rows)
                {
                    bool match = q.Length == 0 || def.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || def.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || def.Description.Contains(q, StringComparison.OrdinalIgnoreCase);
                    row.Visibility = match ? ViewStates.Visible : ViewStates.Gone;
                }
                foreach (var (name, header) in headers)
                    header.Visibility = rows.Any(r => r.Def.Group == name && r.Row.Visibility == ViewStates.Visible) ? ViewStates.Visible : ViewStates.Gone;
            };
        }

        ScrollView scroll = new(c);
        scroll.AddView(box);
        Dialog(c, title)
            .SetView(scroll)!
            .SetPositiveButton("Simpan", (_, _) =>
            {
                Dictionary<string, string> result = new();
                foreach (var (def, _, read) in rows)
                    result[def.Key] = read().Trim();
                onSave(result);
            })!
            .SetNegativeButton("Batal", (_, _) => { })!
            .Show();
    }
}
