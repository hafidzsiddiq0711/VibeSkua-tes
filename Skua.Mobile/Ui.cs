using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Skua.Host;

namespace Skua.Mobile;

/// <summary>Building blocks of the code-built UI (dark, with Skua gold).</summary>
public static class Ui
{
    public static readonly Color Background = Color.ParseColor("#0E0E12");
    public static readonly Color Surface = Color.ParseColor("#17171D");
    public static readonly Color Surface2 = Color.ParseColor("#22222B");
    public static readonly Color Border = Color.ParseColor("#2A2A34");
    public static readonly Color Gold = Color.ParseColor("#FDAF2D");
    public static readonly Color Text = Color.ParseColor("#F2F2F5");
    public static readonly Color Muted = Color.ParseColor("#9A9AA8");
    public static readonly Color Info = Color.ParseColor("#6CC4FF");
    public static readonly Color Good = Color.ParseColor("#5BD38A");
    public static readonly Color Bad = Color.ParseColor("#FF7A70");
    public static readonly Color Night = Color.ParseColor("#B48CFF");

    public static int Dp(Context c, float dp) => (int)(dp * c.Resources!.DisplayMetrics!.Density + 0.5f);

    public static TextView Label(Context c, string text, float sp = 14, Color? color = null, bool bold = false)
    {
        TextView t = new(c) { Text = text };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color ?? Text);
        if (bold)
            t.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Bold);
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

    public static LinearLayout.LayoutParams Margins(Context c, int width, int height, float left, float top, float right, float bottom, float weight = 0)
    {
        LinearLayout.LayoutParams lp = new(width, height, weight);
        lp.SetMargins(Dp(c, left), Dp(c, top), Dp(c, right), Dp(c, bottom));
        return lp;
    }

    public static Android.Widget.Button Button(Context c, string text, Action onClick, bool primary = false, float sp = 13)
    {
        Android.Widget.Button b = new(c) { Text = text };
        b.SetAllCaps(false);
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        b.Background = Rounded(primary ? Gold : Surface2, 12, c, primary ? null : Border);
        b.SetTextColor(primary ? Color.Black : Text);
        b.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        b.SetPadding(Dp(c, 6), 0, Dp(c, 6), 0);
        b.SetMinHeight(Dp(c, 42));
        b.SetMinimumHeight(Dp(c, 42));
        b.StateListAnimator = null;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static LinearLayout Row(Context c, params View[] views)
    {
        LinearLayout row = new(c) { Orientation = Orientation.Horizontal };
        foreach (View v in views)
            row.AddView(v, Margins(c, 0, ViewGroup.LayoutParams.WrapContent, 3, 3, 3, 3, 1f));
        return row;
    }

    /// <summary>A small rounded label (mode, state …).</summary>
    public static TextView Chip(Context c, string text, Color color, bool filled = false)
    {
        TextView chip = Label(c, text, 11, filled ? Color.Black : color, bold: true);
        chip.Background = Rounded(filled ? color : Color.Argb(40, color.R, color.G, color.B), 20, c);
        chip.SetPadding(Dp(c, 9), Dp(c, 3), Dp(c, 9), Dp(c, 3));
        chip.LetterSpacing = 0.04f;
        return chip;
    }

    /// <summary>A selectable filter chip (groups).</summary>
    public static TextView FilterChip(Context c, string text, bool selected, Action onClick)
    {
        TextView chip = Label(c, text, 13, selected ? Color.Black : Text, bold: selected);
        chip.Background = Rounded(selected ? Gold : Surface2, 20, c, selected ? null : Border);
        chip.SetPadding(Dp(c, 14), Dp(c, 7), Dp(c, 14), Dp(c, 7));
        chip.Click += (_, _) => onClick();
        return chip;
    }

    /// <summary>Statistic tile: small caption over a big value. Returns the value view.</summary>
    public static (View Tile, TextView Value) Tile(Context c, string caption, string value)
    {
        LinearLayout tile = new(c) { Orientation = Orientation.Vertical };
        tile.Background = Rounded(Surface, 14, c, Border);
        tile.SetPadding(Dp(c, 12), Dp(c, 10), Dp(c, 12), Dp(c, 10));
        tile.AddView(Label(c, caption.ToUpperInvariant(), 10, Muted, bold: true));
        TextView v = Label(c, value, 17, Text, bold: true);
        v.SetSingleLine(true);
        v.Ellipsize = TextUtils.TruncateAt.End;
        tile.AddView(v);
        return (tile, v);
    }

    /// <summary>Round badge with the account's initial.</summary>
    public static TextView Avatar(Context c, string name, Color color)
    {
        TextView a = Label(c, name.Length > 0 ? name[..1].ToUpperInvariant() : "?", 17, Color.Black, bold: true);
        a.Gravity = GravityFlags.Center;
        GradientDrawable d = new();
        d.SetShape(ShapeType.Oval);
        d.SetColor(color);
        a.Background = d;
        return a;
    }

    public static EditText Field(Context c, string hint, string value, InputTypes type = InputTypes.ClassText)
    {
        EditText e = new(c) { Hint = hint, Text = value, InputType = type };
        e.SetSingleLine(true);
        e.SetTextColor(Text);
        e.SetHintTextColor(Muted);
        return e;
    }

    public static TextView Caption(Context c, string text)
    {
        TextView t = Label(c, text, 12, Muted);
        t.SetPadding(0, Dp(c, 10), 0, 0);
        return t;
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
        box.SetPadding(Dp(c, 20), Dp(c, 8), Dp(c, 20), Dp(c, 8));
        if (note is not null)
        {
            TextView n = Label(c, note, 12, Muted);
            n.SetPadding(0, 0, 0, Dp(c, 8));
            box.AddView(n);
        }

        EditText? search = null;
        if (defs.Count > 12)
        {
            search = Field(c, "Search options …", "");
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
                    TextView h = Label(c, group.ToUpperInvariant(), 11, Gold, bold: true);
                    h.SetPadding(0, Dp(c, 14), 0, Dp(c, 2));
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
                    Switch toggle = new(c) { Text = def.Label, Checked = bool.TryParse(current, out bool b) && b };
                    toggle.SetTextColor(Text);
                    row.AddView(toggle);
                    // Keep the CoreBots style ("True"/"False") when the stored value used it.
                    bool pascal = current.Length > 0 && char.IsUpper(current[0]);
                    read = () => pascal ? (toggle.Checked ? "True" : "False") : (toggle.Checked ? "true" : "false");
                    break;
                }
                case "choice":
                {
                    row.AddView(Label(c, def.Label, 13, Muted));
                    string[] choices = def.Choices ?? Array.Empty<string>();
                    Spinner spinner = new(c);
                    spinner.Adapter = new ArrayAdapter<string>(c, Android.Resource.Layout.SimpleSpinnerDropDownItem, choices);
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
            .SetPositiveButton("Save", (_, _) =>
            {
                Dictionary<string, string> result = new();
                foreach (var (def, _, read) in rows)
                    result[def.Key] = read().Trim();
                onSave(result);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }
}
