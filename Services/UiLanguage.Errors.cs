using System.Text.RegularExpressions;

namespace HistOSets.Services;

internal static partial class UiLanguage
{
    internal static string Error(Exception exception) => Message(exception.Message);

    // The storage layer remains independent of WPF. User-facing validation is
    // translated at the UI boundary; original file names and technical details
    // remain intact. Scientific names supplied by the catalog are never translated.
    internal static string Message(string message)
    {
        if (Locale != "en") return message;
        foreach (var (russian, english) in ErrorMessages)
            if (message.StartsWith(russian, StringComparison.Ordinal))
                return english + message[russian.Length..];
        var version = Regex.Match(message, @"^Версия базы (\d+) не поддерживается этой сборкой \(ожидается (\d+)\)\. Файл не изменён\.$");
        if (version.Success)
            return $"Database version {version.Groups[1].Value} is not supported (expected {version.Groups[2].Value}). The file has not been changed.";
        var nameLimit = Regex.Match(message, @"^Укажите непустое название одной строкой, до (\d+) символов\.$");
        if (nameLimit.Success)
            return $"Enter a nonempty name on one line, up to {nameLimit.Groups[1].Value} characters long.";
        if (!Regex.IsMatch(message, "[А-Яа-яЁё]")) return message;
        return "The operation could not be completed. Diagnostic details:\n" + message;
    }

    private static readonly (string Russian, string English)[] ErrorMessages =
    [
        ("Отсутствуют сведения о каталоге.", "Catalog metadata is missing."),
        ("Группа аналогов с таким названием уже существует.", "An analog group with this name already exists."),
        ("Группа используется объектами. Сначала снимите назначения или перенесите объекты в другую группу.", "This group is assigned to objects. Unassign them or move them to another group first."),
        ("Изображение не принадлежит выбранному объекту или удалено.", "The image does not belong to the selected object or has been deleted."),
        ("Неизвестное состояние объекта.", "Unknown object status."),
        ("Объект пока не готов:\n", "The object is not ready yet. Validation details:\n"),
        ("Объект не найден или удалён.", "The object is missing or has been deleted."),
        ("Типы тегов определены системой. Создайте значение в одном из существующих типов.", "Tag types are predefined. Create a value in one of the existing types."),
        ("Выберите тип тега. Для остальных значений используйте «Прочее».", "Select a tag type. Use Other for values that do not fit another type."),
        ("Системный тип тега нельзя переименовать или удалить.", "A predefined tag type cannot be renamed or deleted."),
        ("Не удалось сохранить изменения каталога. Изменения отменены.\n", "Catalog changes could not be saved. Changes have been rolled back.\n"),
        ("В непустой папке отсутствует catalog.sqlite. Автоматическое создание отменено, чтобы не заменить утраченный каталог. Восстановите копию или выберите пустую папку.", "The selected nonempty folder has no catalog.sqlite. Restore a backup or choose an empty folder; no replacement database has been created."),
        ("Каталог занят другим экземпляром HISTOSETS либо недоступен для записи.", "This catalog is open in another HISTOSETS instance or is not writable."),
        ("Не удалось обновить структуру каталога. Резервная копия: ", "The catalog could not be upgraded. Backup: "),
        ("Выберите отдельную папку каталога.", "Choose a separate catalog folder."),
        ("XML превышает предел 16 МБ.", "The XML file exceeds the 16 MB limit."),
        ("Импорт отменён: сначала исправьте исходные данные.\n", "Import was cancelled. Correct the source data first.\n"),
        ("Для импорта нужен оригинал с нормальной ориентацией и положительными размерами: ", "Import requires a normally oriented original with positive image dimensions: "),
        ("Формат изображения пока не поддерживается: ", "This image format is not supported yet: "),
        ("В каталоге уже есть другие данные. В update2 изменённый XML импортируется в новый пустой каталог кнопкой «Создать из XML». Текущие материалы не изменены.", "The catalog already contains different data. Import the changed XML into a new empty catalog. The current materials have not been changed."),
        ("Не удалось импортировать XML.\n", "The XML could not be imported.\n"),
        ("Повреждён файл существующего каталога: ", "An existing catalog file is damaged: "),
        ("Не подтверждено соответствие геометрии переводов исходного атласа.", "Matching geometry could not be verified for the source atlas translations."),
        ("Карта переводов не покрывает все исходные записи.", "The translation map does not cover all source records."),
        ("Выбранный файл не является базой HISTOSETS.", "The selected file is not a HISTOSETS database."),
        ("Нарушена целостность SQLite. Восстановите резервную копию в другой каталог.", "The SQLite database is damaged. Restore a backup into another folder."),
        ("В базе обнаружена нарушенная связь между записями.", "The database contains a broken record relationship."),
        ("Повреждены сведения о версии каталога.", "The catalog version metadata is damaged."),
        ("Неверный идентификатор каталога.", "The catalog identifier is invalid."),
        ("В базе обнаружен неверный UUID записи.", "The database contains an invalid record UUID."),
        ("В базе обнаружен неполный контур.", "The database contains an incomplete region."),
        ("Недопустимый путь файла в каталоге: ", "Invalid catalog file path: "),
        ("Путь выходит за пределы каталога.", "The path points outside the catalog folder."),
        ("Ссылки на внешние файлы не поддерживаются: ", "External file links are not supported: "),
        ("Файл уже существует. Выберите новое имя резервной копии.", "The file already exists. Choose a new backup file name."),
        ("Архив содержит слишком много файлов либо повторяющиеся пути.", "The archive contains too many files or duplicate paths."),
        ("В архиве нет manifest.json.", "The archive has no manifest.json."),
        ("Слишком большой манифест архива.", "The archive manifest is too large."),
        ("Пустой манифест архива.", "The archive manifest is empty."),
        ("Неподдерживаемая или повреждённая структура резервной копии (предел 64 ГиБ).", "The backup structure is damaged or unsupported (64 GiB limit)."),
        ("Неизвестный тип файла в архиве: ", "Unknown file type in the archive: "),
        ("В архиве отсутствует файл: ", "A file is missing from the archive: "),
        ("Неверный размер файла: ", "Incorrect file size: "),
        ("Файлы архива не соответствуют ссылкам в базе.", "The archive files do not match the database references."),
        ("Резервная копия повреждена или имеет неподдерживаемый формат. Текущий каталог не изменён.", "The backup is damaged or has an unsupported format. The current catalog has not been changed."),
        ("Для восстановления нужна новая или пустая папка. Существующий каталог остаётся на месте.", "Restoration requires a new or empty folder. The existing catalog stays in place."),
        ("Выберите обычную папку, а не ссылку или соединение каталогов.", "Choose a normal folder, not a symbolic link or directory junction."),
        ("Неизвестный путь файла в базе.", "Unknown file path in the database."),
        ("Отсутствует или повреждён файл: ", "Missing or damaged file: "),
        ("Файл превышает указанный размер: ", "The file exceeds its declared size: "),
        ("Не совпадает контрольная сумма файла: ", "File checksum mismatch: "),
        ("Укажите название хотя бы на одном языке.", "Enter a name in at least one language."),
        ("Название должно быть одной строкой длиной до 120 символов.", "The name must be one line, up to 120 characters long."),
        ("Группа с таким названием уже есть.", "A group with this name already exists."),
        ("В этой группе уже есть тег с таким названием.", "This group already contains a tag with that name."),
        ("Сначала перенесите или удалите теги этой группы.", "Move or delete this group's tags first."),
        ("Запись не найдена или удалена. Обновите каталог.", "The record is missing or deleted. Refresh the catalog."),
        ("Не удалось сохранить теги. Изменения отменены.\n", "The tags could not be saved. Changes have been rolled back.\n")
    ];
}
