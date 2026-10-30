// Interface language (RU / EN) of RDR 2 Face: the page, the server messages and the control labels are written in
// Russian; in English every text node, title, placeholder and aria-label is translated as it appears (a MutationObserver).
const EN = {
    "лицевые анимации для RedM": "facial animations for RedM",
    "Режим": "Mode",
    "Персонаж": "Character",
    "Язык": "Language",
    "Перенос из GTA V": "From GTA V",
    "Редактор": "Editor",
    "♂ Мужчина": "♂ Male",
    "♀ Женщина": "♀ Female",
    "Готовый ресурс RedM со всеми лицевыми анимациями библиотеки": "A finished RedM resource with every facial animation of the library",
    "Экспорт ресурса": "Export resource",
    "Из GTA V": "From GTA V",
    "Перетащите .ycd GTA V": "Drop GTA V .ycd files",
    "словари с лицевыми анимациями (facials@…, mp_facial и моды)": "dictionaries with facial animations (facials@…, mp_facial and mods)",
    "Выбрать все": "Select all",
    "Перенести выбранное": "Convert selected",
    "Лицо GTA считается её же выражением лица, движение губ, век, бровей, щёк и челюсти переносится на лицо RDR (121 канал). Процент — насколько точно повторено движение.":
        "The GTA face is evaluated with GTA’s own face expression; the motion of the lips, eyelids, brows, cheeks and jaw is matched on the RDR face (121 controls). The percentage is how closely the motion is repeated.",
    "Лицо": "Face",
    "Поза из игры": "Pose from the game",
    "— выбрать —": "— choose —",
    "Поставить в кадр": "Set on this frame",
    "Сбросить кадр": "Reset frame",
    "Поиск канала: губа, веко, бровь…": "Search a control: lip, eyelid, brow…",
    "Библиотека": "Library",
    "Готовые лицевые анимации. ▶ — посмотреть на голове, ✎ — открыть в редакторе.": "Finished facial animations. ▶ plays one on the head, ✎ opens it in the editor.",
    "Играть / пауза": "Play / pause",
    "Играть": "Play",
    "Длина, с": "Length, s",
    "Ключевой кадр в текущем времени (копия текущей позы)": "A keyframe at the current time (a copy of the current pose)",
    "+ Ключ": "+ Key",
    "Удалить ключ под курсором": "Delete the key under the cursor",
    "Удалить ключ": "Delete key",
    "Переходы": "Transitions",
    "плавные": "smooth",
    "линейные": "linear",
    "Повтор": "Loop",
    "Название (latin): smile_wink": "Name (latin): smile_wink",
    "Новая": "New",
    "Сохранить в библиотеку": "Save to library",
    "Экспорт ресурса RedM": "Export a RedM resource",
    "Все анимации библиотеки — в один ресурс: stream со словарями, команда и экспорты PlayFace / StopFace. Играет только лицо, анимация тела не прерывается.":
        "All the library’s animations in one resource: stream with the dictionaries, a command and the PlayFace / StopFace exports. Only the face plays; the body animation keeps going.",
    "Имя ресурса": "Resource name",
    "Префикс словарей": "Dictionary prefix",
    "Команда": "Command",
    "Сразу положить в папку resources (необязательно)": "Also copy into a resources folder (optional)",
    "Закрыть": "Close",
    "Собрать": "Build",
    "Сборка…": "Building…",
    "Скачать .zip": "Download .zip",
    "между ключами": "between keys",
    "Выберите позу": "Choose a pose",
    "Название: латиница, цифры и _": "Name: latin letters, digits and _",
    "Анимация пустая: подвигайте ползунки": "The animation is empty: move some sliders",
    "Посмотреть": "Play",
    "Открыть в редакторе": "Open in the editor",
    "Удалить": "Delete",
    "Убрать файл": "Remove the file",
    "В этом файле нет лицевых анимаций": "This file has no facial animations",
    "редактор": "editor",
    // server messages
    "id: латиница, цифры и _": "id: latin letters, digits and _",
    "длина: от 2 кадров до 10 минут": "length: from 2 frames to 10 minutes",
    "имя ресурса: латиница, цифры, _ и -": "resource name: latin letters, digits, _ and -",
    "префикс словарей: латиница, цифры, _ и @": "dictionary prefix: latin letters, digits, _ and @",
    "команда: латиница и цифры": "command: latin letters and digits",
    "библиотека пуста": "the library is empty",
    "нужен .ycd файл GTA V (словарь анимаций)": "a GTA V .ycd file (an animation dictionary) is needed",
    "файл больше 200 МБ": "the file is over 200 MB",
    "перенос уже идёт": "a conversion is already running",
    "выберите клипы": "select some clips",
    "клип не двигает лицо MP-персонажа GTA (у сюжетных героев своя голова)": "the clip does not move the GTA MP character’s face (story characters have their own head)",
};

// control groups and labels (make_data.py describe(): "<region>[ слева| справа][, <direction>]")
const GROUPS = {
    "Рот": "Mouth", "Губы": "Lips", "Щёки": "Cheeks", "Глаза": "Eyes", "Брови": "Brows", "Нос": "Nose", "Лоб": "Forehead",
    "Шея": "Neck", "Прочее": "Other",
};
const REGIONS = {
    "Язык": "Tongue", "Уголок рта": "Lip corner", "Верхняя губа": "Upper lip", "Нижняя губа": "Lower lip", "Челюсть": "Jaw",
    "Подбородок": "Chin", "Рот": "Mouth", "Носогубная складка": "Nasolabial fold", "Щека": "Cheek", "Веко": "Eyelid",
    "Под глазом": "Under the eye", "Уголок глаза": "Eye corner", "Глаз": "Eye", "Взгляд": "Gaze", "Лоб / бровь": "Forehead / brow",
    "Бровь": "Brow", "Нос": "Nose", "Ноздря": "Nostril", "Желвак": "Masseter", "Висок": "Temple", "Шея": "Neck",
    "Кадык": "Adam’s apple", "Под подбородком": "Under the chin", "Канал": "Control",
};
const SIDES = { "слева": "left", "справа": "right" };
const DIRS = { "вверх": "up", "вниз": "down", "вперёд": "forward", "назад": "back", "наружу": "out", "внутрь": "in" };
const LABEL_RX = new RegExp(`^(${Object.keys(REGIONS).sort((a, b) => b.length - a.length).map((s) => s.replace(/[/]/g, '\\/')).join('|')})`
    + `(?: (слева|справа))?(?:, (${Object.keys(DIRS).join('|')}))?( \\d+)?( [↕↔])?$`);
function label(s) {
    const m = LABEL_RX.exec(s);
    if (!m) return undefined;
    return REGIONS[m[1]] + (m[2] ? ` ${SIDES[m[2]]}` : '') + (m[3] ? `, ${DIRS[m[3]]}` : '') + (m[4] || '') + (m[5] || '');
}

const EN_PATTERNS = [
    [/^ключ ([\d.]+) с$/, 'key $1 s'],
    [/^([\d.]+) с$/, '$1 s'],
    [/^(\d+) лиц\.$/, '$1 facial'],
    [/^Перенести выбранное \((\d+)\)$/, 'Convert selected ($1)'],
    [/^Сохранено: (.+)$/, 'Saved: $1'],
    [/^Удалить «(.+)»\?$/, 'Delete “$1”?'],
    [/^(.+): нужен \.ycd$/, '$1: a .ycd is needed'],
    [/^Загрузка (.+)…$/, 'Uploading $1…'],
    [/^(.+): лицевых клипов (\d+)$/, '$1: $2 facial clips'],
    [/^Готово: (\d+) анимаций\.$/, 'Done: $1 animations.'],
    [/^Скопировано в (.+)$/, 'Copied to $1'],
    [/^([▾▸]) (.+)$/, (m, a, g) => `${a} ${GROUPS[g] ?? g}`],
    [/^([♂♀]) ([\d.]+) с · (.*)$/, (m, a, d, src) => `${a} ${d} s · ${EN[src] ?? src}`],
    [/^не читается как словарь анимаций GTA V: (.*)$/, 'not readable as a GTA V animation dictionary: $1'],
    [/^OK (\S+): (\d+) кадров, совпадение с GTA (\d+)% \(движение ([\d.]+) мм, отклонение ([\d.]+) мм\) -> (.+)$/,
        'OK $1: $2 frames, $3% match with GTA (motion $4 mm, deviation $5 mm) -> $6'],
    [/^ОШИБКА (\S+): (.*)$/, (m, c, e) => `ERROR ${c}: ${EN[e] ?? e}`],
];

const RU_TO_EN = (s) => {
    const key = s.trim().replace(/\s+/g, ' ');
    if (!key || !/[А-Яа-яЁё]/.test(key)) return s;
    let hit = EN[key] ?? GROUPS[key] ?? label(key);
    if (hit === undefined) {
        for (const [rx, rep] of EN_PATTERNS) if (rx.test(key)) { hit = key.replace(rx, rep); break; }
    }
    if (hit === undefined) return s;
    const lead = /^\s*/.exec(s)[0], tail = /\s*$/.exec(s)[0];
    return lead + hit + tail;
};

export let lang = 'ru';
try { lang = localStorage.getItem('rdrviewer.lang') === 'en' ? 'en' : 'ru'; } catch (e) { /* no storage */ }

const ATTRS = ['title', 'placeholder', 'aria-label', 'alt'];
const origText = new WeakMap(), origAttr = new WeakMap();

function translateNode(node) {
    if (node.nodeType === 3) {
        const ru = origText.has(node) && lang === 'en' ? origText.get(node) : node.data;
        if (lang === 'en') {
            const en = RU_TO_EN(ru);
            if (en !== node.data) { origText.set(node, ru); node.data = en; }
        } else if (origText.has(node)) { node.data = origText.get(node); origText.delete(node); }
        return;
    }
    if (node.nodeType !== 1 || node.tagName === 'SCRIPT' || node.tagName === 'STYLE' || node.tagName === 'TEXTAREA' || node.tagName === 'PRE') return;
    const saved = origAttr.get(node) || {};
    for (const a of ATTRS) {
        if (!node.hasAttribute(a) && !(a in saved)) continue;
        const ru = a in saved ? saved[a] : node.getAttribute(a);
        if (lang === 'en') {
            const en = RU_TO_EN(ru);
            if (en !== node.getAttribute(a)) { saved[a] = ru; node.setAttribute(a, en); }
        } else if (a in saved) { node.setAttribute(a, saved[a]); delete saved[a]; }
    }
    origAttr.set(node, saved);
    for (const c of node.childNodes) translateNode(c);
}

let busy = false;
const observer = new MutationObserver((list) => {
    if (busy || lang !== 'en') return;
    busy = true;
    for (const m of list) {
        if (m.type === 'characterData') { origText.delete(m.target); translateNode(m.target); }
        else if (m.type === 'attributes') {
            const saved = origAttr.get(m.target);
            if (saved) delete saved[m.attributeName];
            translateNode(m.target);
        } else m.addedNodes.forEach(translateNode);
    }
    observer.takeRecords();             // our own replacements
    busy = false;
});

export function setLang(l) {
    lang = l === 'en' ? 'en' : 'ru';
    try { localStorage.setItem('rdrviewer.lang', lang); } catch (e) { /* no storage */ }
    document.documentElement.lang = lang;
    busy = true;
    translateNode(document.body);
    observer.takeRecords();
    busy = false;
}

// a phrase for code that builds strings itself (confirm(), search)
export const t = (s) => (lang === 'en' ? RU_TO_EN(s) : s);

export function startI18n() {
    observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ATTRS });
    setLang(lang);
}
