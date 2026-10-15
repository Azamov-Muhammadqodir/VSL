using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Markup;

namespace VSL.VisualStudio.Views;

/// <summary>
/// The panel's language: English (the texts as written) or Uzbek. Keys are the English texts themselves, so anything
/// without a translation shows in English. Texts the shared view model builds ("Working... (3s)", "Read 12 lines")
/// go through <see cref="Translate"/>, which also knows their patterns.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static IReadOnlyList<(string Code, string Label)> Languages { get; } = [("en", "English"), ("uz", "O‘zbekcha")];

    string _language = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>"en" or "uz".</summary>
    public string Language
    {
        get => _language;
        set
        {
            if (value == _language)
                return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    /// <summary>The short code shown on the language button.</summary>
    public string Code => _language.ToUpperInvariant();

    public string this[string key] => Translate(key);

    public string Translate(string? text)
    {
        if (string.IsNullOrEmpty(text) || _language != "uz")
            return text ?? "";
        if (Uzbek.TryGetValue(text!, out var exact))
            return exact;
        foreach (var (pattern, replace) in UzbekPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success)
                return replace(match);
        }
        return text!;
    }

    static string T(string text) => Instance.Translate(text);

    static readonly Dictionary<string, string> Uzbek = new(StringComparer.Ordinal)
    {
        // Header, accounts, language
        ["Switch or add a Claude account"] = "Claude akkauntini almashtirish yoki qo‘shish",
        ["Account & usage (/usage)"] = "Akkaunt va limitlar (/usage)",
        ["MCP servers (/mcp)"] = "MCP serverlar (/mcp)",
        ["Past conversations"] = "Oldingi suhbatlar",
        ["New conversation"] = "Yangi suhbat",
        ["Language: English / O‘zbekcha"] = "Til: English / O‘zbekcha",
        ["Accounts"] = "Akkauntlar",
        ["Default"] = "Asosiy",
        ["Sign in"] = "Kirish",
        ["Open the Claude CLI for this account in a terminal to sign in"] = "Kirish uchun bu akkaunt bilan Claude CLI’ni terminalda ochish",
        ["Add an account"] = "Akkaunt qo‘shish",
        ["Each account gets its own folder (~/.claude-NAME), like CLAUDE_CONFIG_DIR in a terminal. You sign in once in the terminal that opens."] =
            "Har bir akkauntning o‘z papkasi bo‘ladi (~/.claude-NOM), terminaldagi CLAUDE_CONFIG_DIR kabi. Ochilgan terminalda bir marta kirasiz.",
        ["Name, e.g. work"] = "Nomi, masalan: ish",
        ["Add"] = "Qo‘shish",
        ["Not signed in"] = "Kirilmagan",
        ["Enter a name with letters or digits."] = "Harf yoki raqamli nom kiriting.",

        // Empty conversation and starters
        ["What shall we build today?"] = "Bugun nima quramiz?",
        ["Claude works right in your solution: it reads and edits files, runs commands and explains code. You approve every change."] =
            "Claude to‘g‘ridan-to‘g‘ri yechimingizda ishlaydi: fayllarni o‘qiydi va tahrirlaydi, buyruqlarni bajaradi, kodni tushuntiradi. Har bir o‘zgarishni siz tasdiqlaysiz.",
        ["Explain this file"] = "Faylni tushuntir",
        ["What it does and why"] = "Nima qiladi va nega",
        ["Find and fix bugs"] = "Xatolarni topib tuzat",
        ["In the open file"] = "Ochiq faylda",
        ["Write tests"] = "Test yoz",
        ["Cover the open file"] = "Ochiq fayl uchun",
        ["Tour the solution"] = "Yechim bilan tanishtir",
        ["Projects and structure"] = "Loyihalar va tuzilma",
        ["Explain what the open file does and how it fits into the solution."] = "Ochiq fayl nima qilishini va yechimda qanday o‘rin tutishini tushuntirib ber.",
        ["Look for bugs in the open file and fix the ones you find."] = "Ochiq fayldagi xatolarni qidir va topganlaringni tuzat.",
        ["Write unit tests for the open file."] = "Ochiq fayl uchun unit testlar yoz.",
        ["Give me a short tour of this solution: projects, structure and where to start."] = "Bu yechim bilan qisqacha tanishtir: loyihalar, tuzilma va qayerdan boshlash kerak.",

        // Prompt
        ["Ask Claude to build, fix or explain something…"] = "Claude’dan biror narsa qurish, tuzatish yoki tushuntirishni so‘rang…",
        ["Attach images or files — or just paste or drop them here"] = "Rasm yoki fayl biriktirish — yoki shunchaki shu yerga joylang yoki tashlang",
        ["Claude sees this file and your selection. Click to include or leave it out."] = "Claude bu faylni va belgilangan qismni ko‘radi. Qo‘shish yoki chiqarish uchun bosing.",
        ["How much Claude may do on its own, and how hard it thinks (Shift+Tab to switch)"] = "Claude o‘zi qancha ish qila oladi va qanchalik chuqur o‘ylaydi (Shift+Tab bilan almashtirish)",
        ["Model Claude answers with"] = "Claude javob beradigan model",
        ["Send (Enter)"] = "Yuborish (Enter)",
        ["Stop Claude (Esc)"] = "Claude’ni to‘xtatish (Esc)",
        ["Esc to stop"] = "Esc — to‘xtatish",
        ["Enter"] = "Enter",
        [" to send  ·  "] = " — yuborish  ·  ",
        [" new line  ·  "] = " — yangi qator  ·  ",
        [" commands  ·  "] = " buyruqlar  ·  ",
        [" files"] = " fayllar",
        ["Drop images or files to attach"] = "Biriktirish uchun rasm yoki faylni shu yerga tashlang",
        ["Remove"] = "Olib tashlash",

        // Tokens strip
        ["Context"] = "Kontekst",
        ["Compact"] = "Ixchamlash",
        ["Session"] = "Sessiya",
        ["Week"] = "Hafta",
        ["How much context this conversation uses. Click for the breakdown (/context)."] = "Bu suhbat qancha kontekst ishlatayotgani. Batafsil ko‘rish uchun bosing (/context).",
        ["Summarize the conversation so far so each answer costs fewer tokens (/compact)"] = "Suhbatni xulosalab, har bir javobga kamroq token sarflash (/compact)",
        ["Plan limits: how much is used and when it starts over. Click for details."] = "Tarif limitlari: qancha ishlatilgan va qachon yangilanadi. Batafsil uchun bosing.",
        ["tokens"] = "token",

        // Conversation
        ["Thought process"] = "Fikrlash jarayoni",
        ["Show Claude's reasoning"] = "Claude’ning mulohazasini ko‘rsatish",
        ["Show details"] = "Tafsilotlar",
        ["Open file"] = "Faylni ochish",
        ["Open side-by-side diff"] = "Yonma-yon farqni ochish",
        ["Yes, allow this"] = "Ha, ruxsat beraman",
        ["No"] = "Yo‘q",
        ["No — tell Claude what to do instead"] = "Yo‘q — Claude’ga nima qilish kerakligini yozing",
        ["Claude has a few questions"] = "Claude’ning bir nechta savoli bor",
        ["Something else? Type your own answer"] = "Boshqa javob? O‘zingiz yozing",
        ["Submit answers"] = "Javoblarni yuborish",
        ["Claude's plan"] = "Claude rejasi",
        ["Ready to go ahead?"] = "Davom etamizmi?",
        ["Yes, and auto-accept edits"] = "Ha, tahrirlarni avtomatik qabul qil",
        ["Yes, and manually approve edits"] = "Ha, tahrirlarni o‘zim tasdiqlayman",
        ["Not yet — tell Claude what to change"] = "Hali emas — Claude’ga nimani o‘zgartirishni yozing",
        ["Keep planning"] = "Rejalashtirishni davom ettirish",
        ["Copy"] = "Nusxa",
        ["Copied"] = "Nusxalandi",
        ["Copy the code"] = "Kodni nusxalash",
        ["Attach to the prompt"] = "So‘rovga biriktirish",
        ["Images"] = "Rasmlar",
        ["All files"] = "Barcha fayllar",

        // Not installed, history
        ["Claude Code is not installed"] = "Claude Code o‘rnatilmagan",
        ["Install the Claude Code CLI (in PowerShell), sign in once by running claude in a terminal, then retry."] =
            "Claude Code CLI’ni o‘rnating (PowerShell’da), terminalda claude’ni ishga tushirib bir marta kiring, so‘ng qayta urining.",
        ["Already installed somewhere else? Set the VSL_CLAUDE_PATH environment variable to claude.exe and restart Visual Studio."] =
            "Boshqa joyga o‘rnatilganmi? VSL_CLAUDE_PATH muhit o‘zgaruvchisini claude.exe’ga yo‘naltiring va Visual Studio’ni qayta oching.",
        ["Retry"] = "Qayta urinish",
        ["No conversations in this folder yet."] = "Bu papkada hali suhbatlar yo‘q.",

        // Modes
        ["Modes"] = "Rejimlar",
        ["Shift"] = "Shift",
        [" to switch"] = " — almashtirish",
        ["Manual"] = "Qo‘lda",
        ["Edit automatically"] = "Avtomatik tahrir",
        ["Plan"] = "Reja",
        ["Auto"] = "Avto",
        ["Claude asks before each edit or command."] = "Claude har bir tahrir yoki buyruqdan oldin so‘raydi.",
        ["Claude edits files without asking; commands still need your approval."] = "Claude fayllarni so‘ramasdan tahrirlaydi; buyruqlar uchun baribir ruxsatingiz kerak.",
        ["Claude explores the code and proposes a plan before changing anything."] = "Claude kodni o‘rganadi va biror narsani o‘zgartirishdan oldin reja taklif qiladi.",
        ["Claude runs what passes a safety check and stops to ask about anything risky."] = "Claude xavfsizlik tekshiruvidan o‘tganini bajaradi, xavfli narsalarda to‘xtab so‘raydi.",
        ["Low"] = "Past",
        ["Medium"] = "O‘rta",
        ["High"] = "Yuqori",
        ["Extra high"] = "Juda yuqori",
        ["Max"] = "Maksimal",

        // Usage dialog
        ["Account & Usage"] = "Akkaunt va limitlar",
        ["Close"] = "Yopish",
        ["Loading..."] = "Yuklanmoqda...",
        ["ACCOUNT"] = "AKKAUNT",
        ["USAGE"] = "LIMITLAR",
        ["Manage usage on claude.ai"] = "Limitlarni claude.ai’da boshqarish",
        ["WHAT'S CONTRIBUTING TO YOUR LIMITS USAGE?"] = "LIMITLARNI NIMA SARFLAYAPTI?",
        ["Day"] = "Kun",
        ["Approximate, from local sessions on this machine; other devices and claude.ai are not included."] =
            "Taxminiy, shu kompyuterdagi sessiyalar bo‘yicha; boshqa qurilmalar va claude.ai kirmaydi.",
        ["Skills"] = "Skill’lar",
        ["% of usage"] = "% sarf",
        ["Auth method"] = "Kirish usuli",
        ["Claude.ai account"] = "Claude.ai akkaunti",
        ["API key"] = "API kalit",
        ["Email"] = "Email",
        ["Organization"] = "Tashkilot",
        ["Session (5hr)"] = "Sessiya (5 soat)",
        ["Weekly (7 day)"] = "Haftalik (7 kun)",
        ["Resets soon"] = "Tez orada yangilanadi",
        ["soon"] = "tez orada",

        // MCP
        ["MCP servers"] = "MCP serverlar",
        ["Learn more about MCP"] = "MCP haqida batafsil",
        ["Filter servers..."] = "Serverlarni filtrlash...",
        ["Add server"] = "Server qo‘shish",
        ["Add an MCP server"] = "MCP server qo‘shish",
        ["Name (e.g. github)"] = "Nomi (masalan: github)",
        ["Cancel"] = "Bekor qilish",
        ["Connected"] = "Ulangan",
        ["Connecting"] = "Ulanmoqda",
        ["Disabled"] = "O‘chirilgan",
        ["Needs Auth"] = "Kirish kerak",
        ["Failed - retry"] = "Xato — qayta urinish",
        ["Enable"] = "Yoqish",
        ["Disable"] = "O‘chirish",
        ["Sign in to this server in your browser"] = "Bu serverga brauzerda kiring",
        ["Reconnect"] = "Qayta ulash",
        ["Local (this project, only you)"] = "Lokal (shu loyiha, faqat siz)",
        ["Project (.mcp.json, shared)"] = "Loyiha (.mcp.json, umumiy)",
        ["User (all your projects)"] = "Foydalanuvchi (barcha loyihalaringiz)",
        ["Enter a name without spaces."] = "Bo‘sh joysiz nom kiriting.",
        ["Enter the command that starts the server."] = "Serverni ishga tushiradigan buyruqni kiriting.",
        ["Enter the server's URL."] = "Server URL manzilini kiriting.",
        ["Command, e.g. npx -y @modelcontextprotocol/server-everything"] = "Buyruq, masalan: npx -y @modelcontextprotocol/server-everything",
        ["URL, e.g. https://example.com/mcp"] = "URL, masalan: https://example.com/mcp",

        // Tool calls and outcomes from the view model
        ["Read"] = "O‘qish",
        ["Write"] = "Yozish",
        ["Update"] = "Yangilash",
        ["Edit Notebook"] = "Notebook tahriri",
        ["Search"] = "Qidiruv",
        ["Fetch"] = "Yuklab olish",
        ["Web Search"] = "Internetda qidiruv",
        ["Update Todos"] = "Vazifalar ro‘yxati",
        ["No files found"] = "Fayl topilmadi",
        ["No matches"] = "Mos kelmadi",
        ["(No output)"] = "(Natija yo‘q)",
        ["Denied by you"] = "Siz rad etdingiz",
        ["Allowed"] = "Ruxsat berildi",
        ["Denied"] = "Rad etildi",
        ["Cancelled"] = "Bekor qilindi",
        ["Approved, editing automatically"] = "Tasdiqlandi, avtomatik tahrirlanmoqda",
        ["Approved, asking before edits"] = "Tasdiqlandi, tahrirdan oldin so‘raladi",
        ["Answer each question first."] = "Avval har bir savolga javob bering.",
        ["Allow Claude to run this command?"] = "Claude bu buyruqni bajarsinmi?",
        ["Allow Claude to fetch this page?"] = "Claude bu sahifani yuklab olsinmi?",
        ["Yes, allow all edits during this session"] = "Ha, shu sessiya davomida barcha tahrirlarga ruxsat",
        ["Interrupted."] = "To‘xtatildi.",
        ["Conversation compacted"] = "Suhbat ixchamlandi",
        ["Claude stopped: the maximum number of turns was reached."] = "Claude to‘xtadi: navbatlar soni chegarasiga yetildi.",
        ["Claude Code reported an error."] = "Claude Code xato haqida xabar berdi.",
    };

    static readonly (Regex Pattern, Func<Match, string> Replace)[] UzbekPatterns =
    [
        (new(@"^(Waiting for you|Thinking|Working)\.\.\. \((\d+)s(?: · (.+) tokens)?\)$"), m =>
            (m.Groups[1].Value switch { "Waiting for you" => "Sizni kutyapti", "Thinking" => "O‘ylayapti", _ => "Ishlayapti" })
            + $"... ({m.Groups[2].Value}s" + (m.Groups[3].Success ? $" · {m.Groups[3].Value} token)" : ")")),
        (new(@"^Effort \((.+)\)$"), m => $"Fikrlash darajasi ({T(m.Groups[1].Value)})"),
        (new(@"^Read (\d+) lines?$"), m => $"{m.Groups[1].Value} qator o‘qildi"),
        (new(@"^Wrote (\d+) lines?$"), m => $"{m.Groups[1].Value} qator yozildi"),
        (new(@"^(\d+) additions?, (\d+) removals?$"), m => $"{m.Groups[1].Value} qo‘shildi, {m.Groups[2].Value} o‘chirildi"),
        (new(@"^Found (\d+) files?$"), m => $"{m.Groups[1].Value} ta fayl topildi"),
        (new(@"^Found (\d+) lines?$"), m => $"{m.Groups[1].Value} ta qator topildi"),
        (new(@"^Done \((\d+) tool uses?\)$"), m => $"Tayyor ({m.Groups[1].Value} ta amal)"),
        (new(@"^(\d+) tool uses?$"), m => $"{m.Groups[1].Value} ta amal"),
        (new(@"^\+(\d+) more tool uses$"), m => $"yana {m.Groups[1].Value} ta amal"),
        (new(@"^Allow Claude to create (.+)\?$"), m => $"Claude {m.Groups[1].Value} faylini yaratsinmi?"),
        (new(@"^Allow Claude to overwrite (.+)\?$"), m => $"Claude {m.Groups[1].Value} faylini qayta yozsinmi?"),
        (new(@"^Allow Claude to make this edit to (.+)\?$"), m => $"Claude {m.Groups[1].Value} fayliga shu tahrirni kiritsinmi?"),
        (new(@"^Allow Claude to use (.+)\?$"), m => $"Claude {m.Groups[1].Value} vositasidan foydalansinmi?"),
        (new(@"^A subagent asks: (.+)$", RegexOptions.Singleline), m => "Yordamchi agent so‘rayapti: " + T(m.Groups[1].Value)),
        (new(@"^Yes, and switch to (.+) mode$"), m => $"Ha, va {m.Groups[1].Value} rejimiga o‘tish"),
        (new(@"^Yes, and don't ask again for (.+) in this (session|project)$"), m =>
            $"Ha, va shu {(m.Groups[2].Value == "session" ? "sessiyada" : "loyihada")} {m.Groups[1].Value} uchun boshqa so‘rama"),
        (new(@"^Yes, and allow access to (.+)$"), m => $"Ha, va {m.Groups[1].Value} papkasiga ruxsat"),
        (new(@"^Allowed, (.+)$"), m => "Ruxsat berildi, " + T("Yes, " + m.Groups[1].Value).Replace("Ha, ", "")),
        (new(@"^Denied: (.+)$", RegexOptions.Singleline), m => "Rad etildi: " + m.Groups[1].Value),
        (new(@"^Keep planning: (.+)$", RegexOptions.Singleline), m => "Rejalashtirish davom etadi: " + m.Groups[1].Value),
        (new(@"^Resets in (\d+)([mhd])$"), m => $"{m.Groups[1].Value} {Unit(m.Groups[2].Value)}dan keyin yangilanadi"),
        (new(@"^in (\d+)([mhd])$"), m => $"{m.Groups[1].Value} {Unit(m.Groups[2].Value)}da"),
        (new(@"^(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (\d+), (\d\d:\d\d)$"), m => $"{m.Groups[2].Value}-{Month(m.Groups[1].Value)}, {m.Groups[3].Value}"),
        (new(@"^(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (\d+)$"), m => $"{m.Groups[2].Value}-{Month(m.Groups[1].Value)}"),
        (new(@"^(Resets .+?) · (.+)$"), m => $"{T(m.Groups[1].Value)} · {T(m.Groups[2].Value)}"),
        (new(@"^Resets (.+)$"), m => $"{T(m.Groups[1].Value)} kuni yangilanadi"),
        (new(@"^Weekly (.+)$"), m => $"Haftalik {m.Groups[1].Value}"),
        (new(@"^Week (.+)$"), m => $"Hafta {m.Groups[1].Value}"),
        (new(@"^Local \((\d+)\)$"), m => $"Lokal ({m.Groups[1].Value})"),
        (new(@"^Project \((\d+)\)$"), m => $"Loyiha ({m.Groups[1].Value})"),
        (new(@"^User \((\d+)\)$"), m => $"Foydalanuvchi ({m.Groups[1].Value})"),
        (new(@"^Session \((\d+)\)$"), m => $"Sessiya ({m.Groups[1].Value})"),
        (new(@"^Added (.+)\.$"), m => $"{m.Groups[1].Value} qo‘shildi."),
        (new(@"^Reconnecting (.+)\.\.\.$"), m => $"{m.Groups[1].Value} qayta ulanmoqda..."),
        (new(@"^Finish signing in to (.+) in your browser\.$"), m => $"{m.Groups[1].Value} serveriga kirishni brauzerda yakunlang."),
        (new(@"^Effort set to (.+)\.$"), m => $"Fikrlash darajasi: {T(m.Groups[1].Value)}."),
        (new(@"^Claude Code could not be started: (.+)$", RegexOptions.Singleline), m => "Claude Code ishga tushmadi: " + m.Groups[1].Value),
        (new(@"^Claude Code stopped \(exit code (-?\d+)\)\.(.*)$", RegexOptions.Singleline), m => $"Claude Code to‘xtadi (chiqish kodi {m.Groups[1].Value}).{m.Groups[2].Value}"),
        (new(@"^Usage is not available: (.+)$", RegexOptions.Singleline), m => "Limitlar mavjud emas: " + m.Groups[1].Value),
        (new(@"^MCP servers are not available: (.+)$", RegexOptions.Singleline), m => "MCP serverlar mavjud emas: " + m.Groups[1].Value),
        (new(@"^Sign in to “(.+)” in the terminal that opened, then pick it here again\.$"), m =>
            $"Ochilgan terminalda “{m.Groups[1].Value}” akkauntiga kiring, so‘ng uni shu yerda qayta tanlang."),
        (new(@"^Signing in to the (.+) account in a terminal window\. When you are done, send your prompt here\.$"), m =>
            $"{m.Groups[1].Value} akkauntiga terminal oynasida kirilmoqda. Tugatgach, so‘rovingizni shu yerda yuboring."),
        (new(@"^(.+) is not a PNG, JPEG, GIF or WebP image\.$"), m => $"{m.Groups[1].Value} PNG, JPEG, GIF yoki WebP rasm emas."),
        (new(@"^(.+) is larger than 5 MB\.$"), m => $"{m.Groups[1].Value} 5 MB dan katta."),
        (new(@"^Could not paste: (.+)$"), m => "Joylab bo‘lmadi: " + m.Groups[1].Value),
        (new(@"^Last 24h · (\d+) requests · (\d+) sessions$"), m => $"Oxirgi 24 soat · {m.Groups[1].Value} so‘rov · {m.Groups[2].Value} sessiya"),
        (new(@"^Last 7d · (\d+) requests · (\d+) sessions$"), m => $"Oxirgi 7 kun · {m.Groups[1].Value} so‘rov · {m.Groups[2].Value} sessiya"),
    ];

    static string Month(string month) => month switch
    {
        "Jan" => "yanvar", "Feb" => "fevral", "Mar" => "mart", "Apr" => "aprel", "May" => "may", "Jun" => "iyun",
        "Jul" => "iyul", "Aug" => "avgust", "Sep" => "sentabr", "Oct" => "oktabr", "Nov" => "noyabr", _ => "dekabr",
    };

    static string Unit(string unit) => unit switch { "m" => "daqiqa", "h" => "soat", _ => "kun" };
}

/// <summary><c>{v:L 'English text'}</c>: the text in the panel's language, updating when the language changes.</summary>
public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    // Bound to Language with the key as the parameter (an indexer path would split keys at their commas).
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Loc.Language)) { Source = Loc.Instance, Mode = BindingMode.OneWay, Converter = KeyConverter.Instance, ConverterParameter = Key }
            .ProvideValue(serviceProvider);
}

sealed class KeyConverter : IValueConverter
{
    public static readonly KeyConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.Instance.Translate(parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary><c>{v:Tr Path}</c>: a bound text (from the view model) in the panel's language.</summary>
public sealed class TrExtension(string path) : MarkupExtension
{
    public string Path { get; set; } = path;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = TranslateConverter.Instance, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding(Path) { Mode = BindingMode.OneWay });
        binding.Bindings.Add(new Binding(nameof(Loc.Language)) { Source = Loc.Instance });
        return binding.ProvideValue(serviceProvider);
    }
}

sealed class TranslateConverter : IMultiValueConverter
{
    public static readonly TranslateConverter Instance = new();

    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        Loc.Instance.Translate(values.Length > 0 ? values[0]?.ToString() : null);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
