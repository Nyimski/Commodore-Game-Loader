"""cbmassets.py - downloads artwork and game information for Commodore Game Loader.

Images come from the libretro thumbnail collections (case, title screen and game
screen). Information comes from Wikipedia, with the year and publisher taken
from the image collection's file names when Wikipedia has no article.

Existing files are never replaced, so anything added by hand stays.

    cbmassets.py --jobs jobs.txt --images case,title,screen --info
                 --images-root C64\\Images --info-root C64\\Info --not-found C64\\Settings\\_not_found.txt

jobs.txt holds one game per line: system<TAB>game name[<TAB>title to match it
by, or - for none], where system is C64, VIC20 or C16. A game that matches no
title gets a line "Maybe: game<TAB>title<TAB>title..." of close ones. Progress is printed as "Progress: n/total name".
"""
import argparse
import concurrent.futures
import difflib
import html
import json
import os
import re
import sys
import tempfile
import threading
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

USER_AGENT = 'CommodoreGameLoader/1.0 (https://github.com/Nyimski)'
TIMEOUT = 30
LIST_CACHE_DAYS = 7

SYSTEMS = {
    'C64': {'name': 'Commodore 64', 'collection': 'Commodore - 64', 'repo': 'Commodore_-_64',
            'category': 'Commodore 64 games', 'search': 'Commodore 64'},
    'VIC20': {'name': 'VIC-20', 'collection': 'Commodore - VIC-20', 'repo': 'Commodore_-_VIC-20',
              'category': 'VIC-20 games', 'search': 'VIC-20'},
    'C16': {'name': 'Commodore 16 / Plus/4', 'collection': 'Commodore - Plus-4', 'repo': 'Commodore_-_Plus-4',
            'category': 'Commodore 16 and Plus/4 games', 'search': 'Commodore 16'},
}
IMAGE_TYPES = {'case': ('Case', 'Named_Boxarts'), 'title': ('Title', 'Named_Titles'),
               'screen': ('Screen', 'Named_Snaps')}
IMAGE_EXTENSIONS = ('.png', '.jpg', '.jpeg', '.gif', '.bmp')

ROMAN = {'ii': '2', 'iii': '3', 'iv': '4', 'v': '5', 'vi': '6'}
ARTICLES = {'the', 'a', 'an', 'der', 'die', 'das', 'le', 'la', 'les'}


# --- Name matching ---------------------------------------------------------
# Which side, tape or part a file is, and whether it has a cheat, is not part of
# the game's title: searches leave it out, but the list and the info file keep it,
# so the parts show in order and are not taken for duplicates. It may be in
# brackets or not: 'Elite Side 1', 'Elite - Tape 1 Side A', 'Elite (Side B)'.
PART_MARK = (r'(?:side|tape|part|dis[ck]|cass(?:ette)?|file|load)s?[\s_.-]*(?:\d+|[a-z])(?![a-z0-9])'
             r'(?:\s*(?:&|and|\+|-|to)\s*(?:\d+|[a-z])(?![a-z0-9]))*(?:\s*of\s*\d+)?')
CHEAT_MARK = r'(?:cheat(?:s|ed)?(?:\s*version)?|trainer)(?![a-z0-9])'
TITLE_MARK = rf'(?:{PART_MARK}|{CHEAT_MARK})'
MARK_WORDS = {'disc': 'Disk', 'cas': 'Cassette', 'cass': 'Cassette', 'cassette': 'Cassette'}


def tidy_marker(mark):
    """'side_a' becomes 'Side A', 'tape1 of 2' becomes 'Tape 1 of 2', 'cheats' 'Cheat'."""
    mark = re.sub(r'[\s_.]+', ' ', mark.strip())
    if re.match(r'(cheat|trainer)', mark, re.IGNORECASE):
        return 'Trainer' if mark.lower().startswith('trainer') else 'Cheat'
    found = re.fullmatch(r'([a-z]+?)s?\s*-?\s*(\d+|[a-z])((?:\s*(?:&|and|\+|-|to)\s*(?:\d+|[a-z]))*)(?:\s*of\s*(\d+))?',
                         mark, re.IGNORECASE)
    if not found:
        return mark[:1].upper() + mark[1:]
    word = found.group(1).lower()
    word = MARK_WORDS.get(word, word.capitalize())
    more = re.sub(r'\s*(&|and|\+|-|to)\s*', lambda m: ' & ' if m.group(1) in ('&', 'and', '+') else '-',
                  found.group(3)).upper().replace(' & ', ' & ')
    return f'{word} {found.group(2).upper()}{more}' + (f' of {found.group(4)}' if found.group(4) else '')


def split_title_marks(title):
    """('Elite', ['Tape 1', 'Side A']) from 'Elite - Tape 1 Side A'. A title that is
    nothing but a mark is left whole."""
    marks = []
    core = title
    while True:
        found = re.search(rf'(?:^|[\s_,:;.-]+)({TITLE_MARK})[\s_,:;.-]*$', core, re.IGNORECASE)
        if not found or not core[:found.start()].strip(' _-,:;.'):
            break
        marks.insert(0, tidy_marker(found.group(1)))
        core = core[:found.start()]
    core = core.rstrip(' _-,:;.')
    return (core, marks) if core else (title, [])


def title_marks(name):
    """Every side, tape, part and cheat mark in a file name, in order and tidied:
    the ones after the title, then any bracketed ones."""
    name = os.path.splitext(name)[0] if name.lower().endswith(IMAGE_EXTENSIONS) else name
    head = re.split(r'\s*[(\[]', name, maxsplit=1)[0].strip()
    marks = split_title_marks(head)[1]
    for group in re.findall(r'[(\[]([^)\]]*)[)\]]', name):
        if re.fullmatch(rf'\s*{TITLE_MARK}(?:[\s,;&+]+{TITLE_MARK})*\s*', group, re.IGNORECASE):
            marks += [tidy_marker(mark) for mark in re.findall(TITLE_MARK, group, re.IGNORECASE)]
    seen = set()
    return [mark for mark in marks if not (mark.lower() in seen or seen.add(mark.lower()))]


def with_marks(title, name):
    """The title with the file's marks after it in brackets, any it lacks."""
    for mark in title_marks(name):
        if mark.lower() not in title.lower():
            title += f' ({mark})'
    return title


def base_title(name):
    """The title part of a file name, without regions, years, publishers, flags,
    or which side, tape or part it is."""
    name = os.path.splitext(name)[0] if name.lower().endswith(IMAGE_EXTENSIONS) else name
    return split_title_marks(re.split(r'\s*[(\[]', name, maxsplit=1)[0].strip())[0]


# Words some collections add to say which machine a file is for
MACHINE_WORDS = {'c16', 'c116', 'c264', 'plus4', 'p4', 'c16plus4', 'vic20', 'c64'}
SMALL_WORDS = {'a', 'an', 'and', 'the', 'of', 'at', 'in', 'on', 'to', 'for', 'or', 'by',
               'vs', 'with', 'from', 'into', 'but', 'nor', 'as', 'de'}


def drop_machine_words(words):
    """Words without a machine name at the start or end, such as 'c16' or 'plus 4'."""
    words = list(words)
    while len(words) > 1:
        if words[-1].lower() in MACHINE_WORDS:
            words.pop()
        elif len(words) > 2 and [w.lower() for w in words[-2:]] in (['plus', '4'], ['c16', '4']):
            del words[-2:]
        elif words[0].lower() in MACHINE_WORDS:
            words.pop(0)
        else:
            break
    return words


def tidy_title(name):
    """'jet_set_willy_c16' becomes 'Jet Set Willy'. Names without underscores that
    are not all lower case are left as they are, apart from a machine name."""
    title = base_title(name)
    rest = name[len(title):] if name.startswith(title) else ''
    if '_' in title or (title == title.lower() and re.search('[a-z]', title)):
        words = drop_machine_words(re.sub(r'\s+', ' ', title.replace('_', ' ')).strip().split(' '))
        out = []
        for index, word in enumerate(words):
            lower = word.lower()
            starts_part = index == 0 or out[-1] in ('-', ':') or out[-1].endswith(':')
            if lower in SMALL_WORDS and not starts_part:
                out.append(lower)
            elif lower in ('ii', 'iii', 'iv') or re.fullmatch(r'\d+d', lower):
                out.append(word.upper())
            else:
                out.append(word[:1].upper() + word[1:])
        title = ' '.join(out)
    else:
        title = ' '.join(drop_machine_words(title.split(' ')))
    rest = rest.replace('_', ' ').strip()
    return (title + (' ' + rest if rest else '')).strip() or name


def plain_letters(text):
    """'Hős' becomes 'Hos': accents dropped so names match without them."""
    return ''.join(c for c in unicodedata.normalize('NFKD', text) if not unicodedata.combining(c))


def name_key(title):
    title = plain_letters(title).lower().replace('&', ' and ')
    title = re.sub(r',\s*(the|a|an|der|die|das|le|la|les)$', '', title)
    words = re.findall(r'[a-z0-9]+', title)
    words = drop_machine_words(ROMAN.get(word, word) for word in words)
    while words and words[0] in ARTICLES and len(words) > 1:
        words = words[1:]
    return ''.join(words)


def name_keys(name):
    """Full title key, and the key of the part before any subtitle."""
    title = base_title(name)
    keys = [name_key(title)]
    for separator in (' - ', ': '):
        if separator in title:
            keys.append(name_key(title.split(separator)[0]))
    return [key for key in keys if key]


def preference(name):
    """Lower is better among equally good matches."""
    lower = name.lower()
    score = 0
    score += 4 * len(re.findall(r'\[(?:cr|h|t|f|b|o|a)\b[^\]]*\]', lower))
    score += 3 if '(budget)' in lower else 0
    score += 2 if 'side b' in lower else 0
    score -= 2 if re.search(r'\((?:[^)]*\b(?:europe|gb|uk)\b[^)]*)\)', lower) else 0
    return score, len(name)


class Collection:
    """The file names in one image folder, indexed for matching."""

    def __init__(self, names):
        self.names = names
        self.exact, self.short = {}, {}
        for name in names:
            keys = name_keys(name)
            self.exact.setdefault(keys[0], []).append(name)
            for key in keys[1:]:
                self.short.setdefault(key, []).append(name)
        self.all_keys = list(self.exact)

    def find(self, game):
        keys = name_keys(game)
        if not keys:
            return None
        for table, key in ((self.exact, keys[0]), (self.short, keys[0])) + \
                tuple((self.exact, k) for k in keys[1:]):
            if key in table:
                return min(table[key], key=preference)
        if len(keys[0]) < 5:
            return None                  # short names match far too loosely
        close = difflib.get_close_matches(keys[0], self.all_keys, n=1, cutoff=0.9)
        return min(self.exact[close[0]], key=preference) if close else None

    def maybes(self, game, count=6):
        """Titles that might be this game, best first: close spellings, and names
        that start with it or that it starts with (shortened names)."""
        keys = name_keys(game)
        if not keys or len(keys[0]) < 3:
            return []
        key = keys[0]
        scored = {}
        matcher = difflib.SequenceMatcher(None, '', key)
        for other in self.all_keys:
            score = 0.0
            shorter, longer = sorted((key, other), key=len)
            if len(shorter) >= 4 and longer.startswith(shorter):
                score = 0.75 + 0.2 * len(shorter) / len(longer)
            matcher.set_seq1(other)
            if matcher.real_quick_ratio() > 0.6 and matcher.quick_ratio() > 0.6:
                score = max(score, matcher.ratio())
            if score >= 0.6:
                title = base_title(min(self.exact[other], key=preference))
                scored[title] = max(score, scored.get(title, 0))
        return [title for title, _ in sorted(scored.items(), key=lambda item: -item[1])[:count]]


def tosec_details(name):
    """(year, publisher) from a TOSEC style name such as 'Game (1984)(Ocean)'."""
    found = re.search(r'\((19\d\d|20\d\d)[^)]*\)\(([^)]+)\)', name)
    if not found:
        return None, None
    publisher = found.group(2).strip()
    return found.group(1), (None if publisher in ('-', '') else publisher)


# --- Network ---------------------------------------------------------------
def fetch(url, binary=False):
    request = urllib.request.Request(url, headers={'User-Agent': USER_AGENT})
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        data = response.read()
    return data if binary else data.decode('utf-8', 'replace')


def fetch_json(url, parameters):
    return json.loads(fetch(url + '?' + urllib.parse.urlencode(parameters)))


def list_from_thumbnail_server(system, folder):
    url = 'https://thumbnails.libretro.com/{}/{}/'.format(
        urllib.parse.quote(SYSTEMS[system]['collection']), folder)
    page = fetch(url)
    names = [html.unescape(urllib.parse.unquote(link))
             for link in re.findall(r'href="([^"?/]+\.png)"', page, re.I)]
    if not names:
        raise ValueError('empty listing')
    return names


def list_from_github(system, folder):
    url = 'https://api.github.com/repos/libretro-thumbnails/{}/git/trees/master:{}'.format(
        SYSTEMS[system]['repo'], folder)
    tree = json.loads(fetch(url))
    return [entry['path'] for entry in tree.get('tree', []) if entry['path'].lower().endswith('.png')]


def collection_for(system, folder):
    cache = os.path.join(tempfile.gettempdir(), f'cbm_thumbs_{system}_{folder}.txt')
    if os.path.exists(cache) and time.time() - os.path.getmtime(cache) < LIST_CACHE_DAYS * 86400:
        with open(cache, encoding='utf-8') as source:
            return Collection(source.read().splitlines())
    names, failure = None, None
    for lister in (list_from_thumbnail_server, list_from_github):
        try:
            names = lister(system, folder)
            break
        except Exception as error:
            failure = error
    if names is None:
        raise RuntimeError(f'could not list {SYSTEMS[system]["name"]} images: {failure}')
    with open(cache, 'w', encoding='utf-8') as output:
        output.write('\n'.join(names))
    return Collection(names)


def download_image(system, folder, name):
    sources = (
        'https://thumbnails.libretro.com/{}/{}/{}'.format(
            urllib.parse.quote(SYSTEMS[system]['collection']), folder, urllib.parse.quote(name)),
        'https://raw.githubusercontent.com/libretro-thumbnails/{}/master/{}/{}'.format(
            SYSTEMS[system]['repo'], folder, urllib.parse.quote(name)),
    )
    failure = None
    for url in sources:
        try:
            data = fetch(url, binary=True)
            if data[:8] == b'\x89PNG\r\n\x1a\n':
                return data
            failure = 'not an image'
        except Exception as error:
            failure = error
    raise RuntimeError(failure)


# --- Wikipedia -------------------------------------------------------------
WIKI_API = 'https://en.wikipedia.org/w/api.php'


def article_key(title):
    return name_key(re.sub(r'\s*\([^)]*\)$', '', title))


# Every article in the system's games category, fetched in a few requests and
# kept for a week: a game is then matched on this PC, so one with no article
# costs no requests at all, and one with an article costs one.
WIKI_INDEX_DAYS = 7
WIKI_AT_ONCE = 2              # Wikipedia requests at a time
DOWNLOAD_WORKERS = 4          # games looked up at once
_wiki_indexes = {}
_wiki_lock = threading.Lock()


def wikipedia_index(system):
    """{article key: article title} for the system's games category, or None if
    it cannot be had (then each game is searched for instead)."""
    with _wiki_lock:
        if system in _wiki_indexes:
            return _wiki_indexes[system]
        cache = os.path.join(tempfile.gettempdir(), f'cbm_wiki_{system}.json')
        titles = None
        try:
            if time.time() - os.path.getmtime(cache) < WIKI_INDEX_DAYS * 86400:
                with open(cache, encoding='utf-8') as source:
                    titles = json.load(source)
        except (OSError, ValueError):
            titles = None
        if titles is None:
            try:
                titles, carry_on = [], {}
                while True:
                    reply = fetch_json(WIKI_API, dict({
                        'action': 'query', 'list': 'categorymembers', 'format': 'json', 'cmlimit': 500,
                        'cmnamespace': 0, 'cmtitle': f'Category:{SYSTEMS[system]["category"]}'}, **carry_on))
                    titles += [member['title'] for member in reply['query']['categorymembers']]
                    if 'continue' not in reply:
                        break
                    carry_on = reply['continue']
                with open(cache, 'w', encoding='utf-8') as output:
                    json.dump(titles, output)
            except Exception:
                titles = None
        index = None
        if titles:
            index = {}
            for title in titles:
                index.setdefault(article_key(title), title)
        _wiki_indexes[system] = index
        return index


def find_article(game, system):
    index = wikipedia_index(system)
    if index is not None:
        keys = name_keys(game)
        for key in keys:
            if key in index:
                return index[key]
        if keys and len(keys[0]) >= 5:
            close = difflib.get_close_matches(keys[0], list(index), n=1, cutoff=0.9)
            if close:
                return index[close[0]]
        return None
    return search_article(game, system)


def search_article(game, system):
    """find_article without the category list: a search, then a check that the
    results are in the system's games category."""
    keys = name_keys(game)
    title = base_title(game)
    results = fetch_json(WIKI_API, {
        'action': 'query', 'list': 'search', 'format': 'json', 'srlimit': 10,
        'srsearch': f'{title} video game {SYSTEMS[system]["search"]}'})['query']['search']
    candidates = [r['title'] for r in results
                  if article_key(r['title']) in keys or
                  difflib.SequenceMatcher(None, article_key(r['title']), keys[0]).ratio() >= 0.9]
    if not candidates:
        return None
    categories = fetch_json(WIKI_API, {
        'action': 'query', 'format': 'json', 'prop': 'categories', 'redirects': 1,
        'titles': '|'.join(candidates), 'clcategories': f'Category:{SYSTEMS[system]["category"]}',
        'cllimit': 50})['query']
    tagged = {page['title'] for page in categories.get('pages', {}).values() if page.get('categories')}
    redirect = {r['from']: r['to'] for r in categories.get('redirects', [])}
    for candidate in candidates:
        if redirect.get(candidate, candidate) in tagged:
            return redirect.get(candidate, candidate)
    return None


def clean_wikitext(text):
    text = re.sub(r'<ref[^>]*/>', '', text)
    text = re.sub(r'<ref[^>]*>.*?</ref>', '', text, flags=re.S)
    text = re.sub(r'<!--.*?-->', '', text, flags=re.S)
    text = re.sub(r'<br\s*/?>', ', ', text)
    text = re.sub(r'\[\[(?:[^|\]]*\|)?([^\]]*)\]\]', r'\1', text)
    # Date templates keep their values as arguments
    text = re.sub(r'\{\{\s*(?:vgrelease|video game release|vgrtbl|start date|release date|film date)\s*\|([^{}]*)\}\}',
                  lambda m: ' '.join(p.strip() for p in m.group(1).split('|') if '=' not in p),
                  text, flags=re.I)
    for _ in range(3):   # list templates hold their items as arguments
        text = re.sub(r'\{\{\s*(?:ubl|unbulleted list|plainlist|flatlist|hlist|collapsible list)\s*\|([^{}]*)\}\}',
                      lambda m: ', '.join(p.strip() for p in m.group(1).split('|') if '=' not in p),
                      text, flags=re.I)
        text = re.sub(r'\{\{[^{}]*\}\}', ' ', text)
    text = re.sub(r"'{2,}", '', text)
    text = re.sub(r'<[^>]+>', '', text)
    text = re.sub(r'^\s*[*#]\s*', '', text, flags=re.M)
    text = re.sub(r'\s*\n\s*', ', ', text.strip())
    text = re.sub(r'(,\s*)+', ', ', text)
    return re.sub(r'\s+', ' ', text).strip(' ,')


def infobox_fields(wikitext):
    fields, depth, current, buffer = {}, 0, None, []
    start = re.search(r'\{\{\s*Infobox', wikitext, re.I)
    if not start:
        return fields
    index = start.end()
    while index < len(wikitext):
        pair = wikitext[index:index + 2]
        if pair in ('{{', '[['):
            depth += 1
            buffer.append(pair)
            index += 2
            continue
        if pair in ('}}', ']]'):
            if depth == 0 and pair == '}}':
                break
            depth -= 1
            buffer.append(pair)
            index += 2
            continue
        if wikitext[index] == '|' and depth == 0:
            if current:
                fields[current] = ''.join(buffer)
            match = re.match(r'\|\s*([\w ]+?)\s*=', wikitext[index:])
            current = match.group(1).lower() if match else None
            buffer = []
            index += match.end() if match else 1
            continue
        buffer.append(wikitext[index])
        index += 1
    if current:
        fields[current] = ''.join(buffer)
    return fields


def wikipedia_info(game, system):
    article = find_article(game, system)
    if not article:
        return None
    pages = fetch_json(WIKI_API, {
        'action': 'query', 'format': 'json', 'redirects': 1, 'titles': article,
        'prop': 'extracts|revisions', 'exintro': 1, 'explaintext': 1,
        'rvprop': 'content', 'rvslots': 'main', 'rvsection': 0})['query']['pages']
    page = next(iter(pages.values()))
    wikitext = page.get('revisions', [{}])[0].get('slots', {}).get('main', {}).get('*', '')
    fields = infobox_fields(wikitext)

    def field(*names):
        for name in names:
            value = clean_wikitext(fields.get(name, ''))
            if value:
                return value
        return ''

    released = field('released', 'release')
    years = re.findall(r'\b(19[789]\d|20[0-2]\d)\b', released)
    about = re.sub(r'\n{2,}', '\n\n', page.get('extract', '').strip())
    return {
        'Publisher': field('publisher', 'publishers'),
        'Developer': field('developer', 'developers'),
        'Year': min(years) if years else '',
        'Genre': field('genre', 'genres'),
        'Players': field('modes', 'mode'),
        'About': about,
        'Source': 'Wikipedia, https://en.wikipedia.org/wiki/' + urllib.parse.quote(page['title'].replace(' ', '_'))
                  + ' (text under CC BY-SA 4.0)',
    }


def write_info(path, game, system, info, fallback):
    lines = [
        f'Title: {with_marks(info.get("Title") or base_title(game), game)}',
        f'System: {SYSTEMS[system]["name"]}',
        f'Publisher: {info.get("Publisher") or fallback.get("Publisher", "")}',
        f'Developer: {info.get("Developer", "")}',
        f'Year: {info.get("Year") or fallback.get("Year", "")}',
        f'Genre: {info.get("Genre", "")}',
        f'Players: {info.get("Players", "")}',
    ] + ([f'Memory: {info["Memory"]}'] if info.get('Memory') else []) + [
        f'{label}: {info[label]}' for label in ('Music', 'Review') if info.get(label)
    ] + [
        'Controls: ',
        'Loading: ',
        '',
        'About:',
        info.get('About') or 'No description found yet. Add your own notes here.',
    ]
    sources = info.get('Sources') or ([info['Source']] if info.get('Source') else [])
    if sources:
        lines += [''] + [f'Source: {source}' for source in sources]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8') as output:
        output.write('\r\n'.join(line.rstrip() for line in lines) + '\r\n')


# --- Filling gaps in existing info files ---------------------------------------
# An info file that already exists is never replaced: only its empty fields are
# filled, and missing lines added, from whatever the sources have. Anything typed
# by hand stays. A game counts as having gaps when one of GAP_FIELDS is empty or
# its description is missing; the app checks the same way to choose the games.
GAP_FIELDS = ('Year', 'Publisher', 'Developer', 'Genre', 'Players')
EXTRA_FIELDS = ('Memory', 'Music', 'Review')       # filled when a source has them
NO_DESCRIPTION = 'No description found yet. Add your own notes here.'


def read_info_file(path):
    with open(path, 'rb') as source:
        raw = source.read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    text = raw.decode('utf-8-sig', 'replace')
    return text.replace('\r\n', '\n').replace('\r', '\n').split('\n'), bom


def info_layout(lines):
    """(index of the About: line or None, {field: line index} of the detail lines,
    (first, end) of the description lines)."""
    about = next((i for i, line in enumerate(lines) if line.strip().lower().startswith('about:')), None)
    details = {}
    for index, line in enumerate(lines[:about] if about is not None else lines):
        found = re.match(r'\s*([A-Za-z][A-Za-z ]*?)\s*:(.*)$', line)
        if found and found.group(1).lower() != 'source':
            details.setdefault(found.group(1).strip().title(), index)
    if about is None:
        return None, details, (len(lines), len(lines))
    end = len(lines)
    for index in range(about + 1, len(lines)):
        if lines[index].strip().lower().startswith('source:'):
            end = index
            break
    return about, details, (about + 1, end)


def detail_value(line):
    return line.split(':', 1)[1].strip() if ':' in line else ''


def info_gaps(path):
    """The GAP_FIELDS that are empty or missing, and 'About' if there is no description."""
    lines, _ = read_info_file(path)
    about, details, (first, end) = info_layout(lines)
    gaps = {field for field in GAP_FIELDS if field not in details or not detail_value(lines[details[field]])}
    inline = detail_value(lines[about]) if about is not None else ''
    description = ' '.join([inline] + [line.strip() for line in lines[first:end]]).strip()
    if not description or description == NO_DESCRIPTION:
        gaps.add('About')
    return gaps


def fill_info(path, info, sources):
    """Fills the file's empty fields from info, adding missing detail lines and a
    description if it has none. Returns the fields filled."""
    lines, bom = read_info_file(path)
    while lines and lines[-1] == '':
        lines.pop()
    filled = []
    about, details, _ = info_layout(lines)
    # New lines go after the game's own details (before Controls, Loading and the like)
    known = [index for field, index in details.items() if field in ('Title', 'System') + GAP_FIELDS + EXTRA_FIELDS]
    if known:
        after = max(known) + 1
    else:
        after = max(details.values()) + 1 if details else (about if about is not None else len(lines))
    for field in GAP_FIELDS + EXTRA_FIELDS:
        value = str(info.get(field) or '').strip()
        if not value:
            continue
        if field in details:
            if not detail_value(lines[details[field]]):
                lines[details[field]] = f'{field}: {value}'
                filled.append(field)
        else:
            lines.insert(after, f'{field}: {value}')
            after += 1
            filled.append(field)
    about_text = str(info.get('About') or '').strip()
    if about_text:
        about, details, (first, end) = info_layout(lines)
        inline = detail_value(lines[about]) if about is not None else ''
        description = ' '.join([inline] + [line.strip() for line in lines[first:end]]).strip()
        if not description or description == NO_DESCRIPTION:
            body = about_text.split('\n')
            if about is None:
                lines += ['', 'About:'] + body
            else:
                lines[about] = 'About:'
                lines[first:end] = body + ([''] if end < len(lines) else [])
            filled.append('About')
    if not filled:
        return []
    present = {line.strip().lower() for line in lines}
    new_sources = [f'Source: {source}' for source in sources if f'source: {source}'.lower() not in present]
    if new_sources:
        if not any(line.strip().lower().startswith('source:') for line in lines):
            lines.append('')
        lines += new_sources
    data = ('\r\n'.join(lines) + '\r\n').encode('utf-8')
    with open(path, 'wb') as output:
        output.write((b'\xef\xbb\xbf' if bom else b'') + data)
    return filled


# --- Plus/4 World (C16 and Plus/4) --------------------------------------------
# Its tape list gives each game's title, year, 16K or 64K, the name of its tape
# download (which is usually the name of the user's file too) and its page. The
# game's page has its category, publisher, coders, notes and pictures. The list
# is fetched once a week; game pages only for games being downloaded for.
PLUS4WORLD = 'https://plus4world.powweb.com'
PLUS4WORLD_PAUSE = 0.5          # seconds between pages, to be polite


def strip_tags(text):
    text = re.sub(r'<br\s*/?>', '\n', text, flags=re.IGNORECASE)
    return html.unescape(re.sub(r'<[^>]+>', '', text)).replace('\xa0', ' ').strip()


def plus4world_rows(page):
    """Rows of a tape list page: dicts of file, title, year, memory, page."""
    chunks = re.split(r'<tr\b', page, flags=re.IGNORECASE)
    if sum('/tap/' in chunk for chunk in chunks) < 2:
        # Not a table: cut the page at each game's own page link instead
        starts = [m.start() for m in re.finditer(r'<a[^>]+href="[^"]*/software/', page, re.IGNORECASE)]
        chunks = [page[a:b] for a, b in zip(starts, starts[1:] + [len(page)])]
    rows = []
    for chunk in chunks:
        tape = re.search(r'/tap/([^"\'<>]+?)\.zip', chunk, re.IGNORECASE)
        link = re.search(r'<a[^>]+href="([^"]*/software/[^"#?]+)"[^>]*>(.*?)</a>', chunk, re.IGNORECASE | re.DOTALL)
        if not tape or not link:
            continue
        title = strip_tags(link.group(2))
        if not title:
            others = re.findall(r'<a[^>]+href="[^"]*/software/[^"#?]+"[^>]*>(.*?)</a>', chunk, re.IGNORECASE | re.DOTALL)
            title = next((strip_tags(t) for t in others if strip_tags(t)), '')
        page_url = urllib.parse.urljoin(PLUS4WORLD + '/', html.unescape(link.group(1)))
        if not title:
            title = urllib.parse.unquote(page_url.rsplit('/', 1)[-1]).replace('_', ' ')
        cells = [strip_tags(cell) for cell in re.findall(r'<td[^>]*>(.*?)</td>', chunk, re.IGNORECASE | re.DOTALL)]
        text = ' | '.join(cells) if cells else strip_tags(chunk)
        year = re.search(r'\b(19[6-9]\d|20[0-3]\d)\b', text)
        memory = re.search(r'\b(16|32|64)\s*K\b', text, re.IGNORECASE)
        rows.append({'file': urllib.parse.unquote(tape.group(1)).rsplit('/', 1)[-1], 'title': title,
                     'year': year.group(1) if year else '', 'memory': f'{memory.group(1)}K' if memory else '',
                     'page': page_url})
    return rows


class Plus4World:
    def __init__(self, rows):
        self.rows = rows
        self.by_file = {}
        for row in rows:
            self.by_file.setdefault(row['file'].lower(), row)
        self.by_name = {}
        for row in rows:
            self.by_name.setdefault(f"{row['title']} ({row['year'] or 'none'})", row)
        self.collection = Collection(list(self.by_name))

    def find(self, game, look):
        """The row for a game: by its file name first, then by title."""
        for name in (game, game.replace(' ', '_'), look.replace(' ', '_')):
            row = self.by_file.get(name.lower())
            if row:
                return row
        match = self.collection.find(look)
        return self.by_name.get(match) if match else None


def plus4world_index():
    cache = os.path.join(tempfile.gettempdir(), 'cbm_plus4world_tapes.json')
    if os.path.exists(cache) and time.time() - os.path.getmtime(cache) < LIST_CACHE_DAYS * 86400:
        with open(cache, encoding='utf-8') as source:
            return Plus4World(json.load(source))
    rows = plus4world_rows(fetch(PLUS4WORLD + '/tapes/%25'))
    if len(rows) < 100:
        # The page of every tape did not work: go letter by letter
        rows = []
        for letter in '0ABCDEFGHIJKLMNOPQRSTUVWXYZ':
            rows += plus4world_rows(fetch(f'{PLUS4WORLD}/tapes/{letter}'))
            time.sleep(PLUS4WORLD_PAUSE)
    if not rows:
        raise RuntimeError('Plus/4 World tape list could not be read')
    with open(cache, 'w', encoding='utf-8') as output:
        json.dump(rows, output)
    return Plus4World(rows)


def plus4world_field(page, label):
    """The text in the cell after a label such as 'Category' on a game's page."""
    found = re.search(r'>\s*' + re.escape(label) + r'\s*:?\s*<', page, re.IGNORECASE)
    if not found:
        return ''
    cell = re.search(r'<t[dh][^>]*>(.*?)</t[dh]>', page[found.end():found.end() + 3000], re.IGNORECASE | re.DOTALL)
    return re.sub(r'[ \t]+', ' ', strip_tags(cell.group(1))) if cell else ''


def first_year(text):
    found = re.search(r'\b(19[6-9]\d|20[0-3]\d)\b', text)
    return found.group(1) if found else ''


def memory_size(text):
    found = re.search(r'\b(16|32|64)\s*K\b', text, re.IGNORECASE)
    return f'{found.group(1)}K' if found else ''


def plus4world_details(row):
    """Info and picture addresses from a game's own page."""
    page = fetch(row['page'])
    category = plus4world_field(page, 'Category')
    genre = category.split('/', 1)[1] if category.lower().startswith('game/') else category
    info = {
        'Title': plus4world_field(page, 'Title') or row['title'],
        'Year': first_year(plus4world_field(page, 'Release Date')) or row['year'],
        'Publisher': plus4world_field(page, 'Distributed by') or plus4world_field(page, 'Released by'),
        'Developer': plus4world_field(page, 'Code by'),
        'Genre': genre.replace('/', ', '),
        'Memory': memory_size(plus4world_field(page, 'Size')) or row['memory'],
        'About': plus4world_field(page, 'Notes'),
        'Source': row['page'],
    }
    pictures = [urllib.parse.urljoin(PLUS4WORLD + '/', html.unescape(path)) for path in
                re.findall(r'["\'](/?dl/(?:screenshots|covers)/[^"\'\s>]+?\.(?:gif|jpe?g|png))', page, re.IGNORECASE)]
    pictures = list(dict.fromkeys(pictures))
    shots = [p for p in pictures if '/screenshots/' in p]
    covers = [p for p in pictures if '/covers/' in p and not re.search(r'_(info|cassette|tape|back|inlay)', p.rsplit('/', 1)[-1])]
    covers.sort(key=lambda p: ('large' not in p, '_alt' in p, len(p)))
    images = {
        'title': next((p for p in shots if re.search(r'_title\.', p)), None),
        'screen': next((p for p in shots if not re.search(r'_title\.', p)), None),
        'case': covers[0] if covers else None,
    }
    return info, images


# --- The Commodore Zone (C64) -------------------------------------------------
# Its C64 game database gives year, publisher, programmer, musician, genre,
# players and magazine review for each game, and a screenshot. One search for
# the game's name answers everything: the details are in the result's keyword
# links, each tagged with the field it belongs to. Only these pages and the
# screenshot are read; nothing else on the site. The site only answers over
# plain http. Answers are kept for CZONE_CACHE_DAYS, found or not.
CZONE = 'http://www.the-commodore-zone.com'
CZONE_PAUSE = 1.0               # seconds between requests, to be polite
CZONE_CACHE_DAYS = 30
CZONE_FIELDS = {'9': 'publisher', '22': 'programmer', '35': 'musician', '48': 'genre',
                '49': 'options', '50': 'media', '51': 'review'}


def czone_entries(page):
    """Every game on a search or list page: dicts of id, title, picture and the
    keyword values of each field."""
    heads = [m for m in re.finditer(
        r"<a href='/user/index\.php\?p=getitem&(?:amp;)?db_id=1&(?:amp;)?item_id=(\d+)'[^>]*>(.*?)</a>",
        page, re.IGNORECASE | re.DOTALL) if strip_tags(m.group(2)).lower() != 'view game']
    entries = []
    for head, following in zip(heads, heads[1:] + [None]):
        chunk = page[head.end():following.start() if following else len(page)]
        fields = {}
        for column, value in re.findall(r'column=(\d+)text&(?:amp;)?keywords=[^"\']*["\'][^>]*>(.*?)</a>',
                                        chunk, re.IGNORECASE | re.DOTALL):
            name = CZONE_FIELDS.get(column)
            value = strip_tags(value)
            if name and value and value not in fields.setdefault(name, []):
                fields[name].append(value)
        picture = re.search(r'src="([^"]*/image/data/\d+/)thumbs/([^"/]+\.(?:gif|png|jpe?g))"', chunk, re.IGNORECASE)
        entries.append({
            'id': head.group(1), 'title': strip_tags(head.group(2)), 'fields': fields,
            'picture': urllib.parse.urljoin(CZONE + '/', picture.group(1) + picture.group(2)) if picture else None,
        })
    return entries


CZONE_LETTERS = range(1, 29)      # the site's 0-9, A to Z and Collections lists
CZONE_PER_PAGE = 500


def czone_letter_page(letter, start):
    query = urllib.parse.urlencode({'db_id': 1, 'cat_id': letter, 'display': CZONE_PER_PAGE,
                                    'p': 'getcat', 'rowstart': start})
    return fetch(f'{CZONE}/user/index.php?{query}', binary=True).decode('latin-1')


def czone_page_count(page):
    found = re.search(r'Viewing\s+page\s+\d+\s+of\s+(\d+)', page, re.IGNORECASE)
    return int(found.group(1)) if found else 1


def czone_whole_list(say=print):
    """Every game in the database, read a letter at a time in big pages (about
    seventy requests), or None if the site only gives a few games a page, in
    which case each game is searched for instead."""
    entries = []
    for letter in CZONE_LETTERS:
        start = 0
        while True:
            page = czone_letter_page(letter, start)
            time.sleep(CZONE_PAUSE)
            found = czone_entries(page)
            pages = czone_page_count(page)
            if letter == CZONE_LETTERS[0] and start == 0 and pages > 1 and len(found) <= 6:
                return None             # big pages not honoured
            entries += found
            start += CZONE_PER_PAGE
            if not found or start >= pages * CZONE_PER_PAGE:
                break
        say(f'Note: Reading The Commodore Zone list: {len(entries):,} games so far', flush=True)
    return entries


def czone_page(keywords):
    query = urllib.parse.urlencode({
        'db_id': 1, 'p': 'getdatabasesearch', 'category': 'all', 'column': '7text',
        'keywords': keywords, 'ordercolumn': '7text', 'orderdirection': 'asc', 'display': 50})
    return fetch(f'{CZONE}/user/index.php?{query}', binary=True).decode('latin-1')


class CommodoreZone:
    def __init__(self, say=print):
        self.cache_path = os.path.join(tempfile.gettempdir(), 'cbm_czone_games.json')
        self.list_path = os.path.join(tempfile.gettempdir(), 'cbm_czone_list.json')
        self.cache = {}
        self.disabled = False
        self.say = say
        self.lock = threading.Lock()
        self.listed = None              # (Collection, {title: entry}) from the whole list
        self.list_tried = False
        try:
            with open(self.cache_path, encoding='utf-8') as source:
                self.cache = json.load(source)
        except (OSError, ValueError):
            pass
        oldest = time.time() - CZONE_CACHE_DAYS * 86400
        self.cache = {key: value for key, value in self.cache.items() if value.get('when', 0) > oldest}

    def save(self):
        with self.lock:
            answers = dict(self.cache)
        try:
            with open(self.cache_path, 'w', encoding='utf-8') as output:
                json.dump(answers, output)
        except OSError:
            pass

    def whole_list(self):
        """The site's whole game list, read once and kept for CZONE_CACHE_DAYS."""
        if self.list_tried:
            return self.listed
        self.list_tried = True
        entries = None
        try:
            if time.time() - os.path.getmtime(self.list_path) < CZONE_CACHE_DAYS * 86400:
                with open(self.list_path, encoding='utf-8') as source:
                    entries = json.load(source)
        except (OSError, ValueError):
            entries = None
        if entries is None:
            self.say('Note: reading The Commodore Zone\'s game list (once a month, a minute or two)', flush=True)
            try:
                entries = czone_whole_list(self.say)
            except Exception as error:
                self.say(f'Warning: The Commodore Zone list could not be read ({error}): searching game by game', flush=True)
                entries = None
            if entries:
                try:
                    with open(self.list_path, 'w', encoding='utf-8') as output:
                        json.dump(entries, output)
                except OSError:
                    pass
        if entries:
            by_title = {}
            for entry in sorted(entries, key=lambda e: int(e['id'])):
                by_title.setdefault(entry['title'], entry)
            self.listed = (Collection(list(by_title)), by_title)
        return self.listed

    def find(self, look):
        """The best entry for a game, or None: from the whole list when the site
        gives it, otherwise by searching its exact name, then its words with
        wildcards between them (for punctuation and spelling)."""
        with self.lock:
            listed = self.whole_list()
            if listed is not None:
                match = listed[0].find(look)
                return listed[1][match] if match else None
            return self.search(look)

    def search(self, look):
        key = name_key(look)
        if not key:
            return None
        if key in self.cache:
            return self.cache[key].get('entry')
        words = [w for w in re.findall(r'[A-Za-z0-9]+', plain_letters(base_title(look)))
                 if w.lower() not in ARTICLES and w.lower() not in MACHINE_WORDS
                 and w.lower() not in SMALL_WORDS]          # 'and' may be '&' on the site
        tries = [base_title(look)]
        if words:
            tries.append('%' + '%'.join(words) + '%')
        found = None
        for keywords in tries:
            entries = czone_entries(czone_page(keywords))
            time.sleep(CZONE_PAUSE)
            if not entries:
                continue
            by_title = {}
            for entry in entries:
                by_title.setdefault(entry['title'], entry)
            match = Collection(list(by_title)).find(look)
            if match:
                found = by_title[match]
                break
        self.cache[key] = {'when': time.time(), 'entry': found}
        return found


def czone_info(entry):
    """Info fields from a Commodore Zone entry, in the names write_info uses."""
    fields = entry['fields']
    publisher_values = fields.get('publisher', [])
    year = next((v for v in publisher_values if re.fullmatch(r'(19|20)\d\d', v)), '')
    publisher = next((v for v in publisher_values if v != year), '')
    review = fields.get('review', [])
    if review and re.fullmatch(r'(19|20)\d\d', review[-1]):
        review = review[:-1] + [f'({review[-1]})']
    players = next((v for v in fields.get('options', []) if 'player' in v.lower()), '')
    return {
        'Year': year,
        'Publisher': publisher,
        'Developer': ', '.join(fields.get('programmer', [])),
        'Genre': ', '.join(fields.get('genre', [])),
        'Players': players,
        'Music': ', '.join(fields.get('musician', [])),
        'Review': ' '.join(review),
        'Source': f'The Commodore Zone, {CZONE}/user/index.php?p=getitem&db_id=1&item_id={entry["id"]}',
    }


# --- TheGamesDB (needs the user's own API key) --------------------------------
# Used after libretro and Plus/4 World and before Wikipedia. The key comes from
# the app's Settings through the CBM_TGDB_KEY environment variable. Its lists of
# platforms, publishers, developers and genres are fetched once a week; every
# game looked up costs one request of the key's monthly allowance (two if its
# screenshots are wanted too).
TGDB_API = 'https://api.thegamesdb.net/v1'
TGDB_PAUSE = 1.5                # seconds between requests, to stay under its rate limit
TGDB_RETRIES = 3                # tries after a "too many requests" (429) reply
TGDB_GAME_CACHE_DAYS = 30       # a game's answer is kept this long, found or not
TGDB_PLATFORM_NAMES = {
    'C64': ('commodore 64', 'c64'),
    'VIC20': ('vic-20', 'vic 20', 'vic20'),
    'C16': ('commodore 16', 'plus/4', 'plus 4', 'plus4', 'c16'),
}


class TheGamesDB:
    def __init__(self, key):
        self.key = key
        self.lists = {}
        self.disabled = None         # the reason, once a request shows the key is not usable
        self.last_request = 0.0
        self.cache_path = os.path.join(tempfile.gettempdir(), 'cbm_tgdb_games.json')
        try:
            with open(self.cache_path, encoding='utf-8') as source:
                self.cache = json.load(source)
        except (OSError, ValueError):
            self.cache = {}
        cutoff = time.time() - TGDB_GAME_CACHE_DAYS * 86400
        self.cache = {k: v for k, v in self.cache.items() if v.get('when', 0) >= cutoff}

    def save_cache(self):
        try:
            with open(self.cache_path, 'w', encoding='utf-8') as output:
                json.dump(self.cache, output)
        except OSError:
            pass

    def get(self, path, **parameters):
        if self.disabled:
            raise RuntimeError(self.disabled)
        parameters['apikey'] = self.key
        for attempt in range(TGDB_RETRIES + 1):
            wait = TGDB_PAUSE - (time.time() - self.last_request)
            if wait > 0:
                time.sleep(wait)
            self.last_request = time.time()
            try:
                reply = fetch_json(f'{TGDB_API}/{path}', parameters)
                break
            except urllib.error.HTTPError as error:
                if error.code in (401, 403):
                    self.disabled = 'the API key was not accepted (it may not be active yet)'
                    raise RuntimeError(self.disabled)
                if error.code != 429:
                    raise
                if attempt == TGDB_RETRIES:
                    self.disabled = ('TheGamesDB is limiting requests (error 429). The rest are skipped '
                                     'this time; downloading again later carries on where this left off')
                    raise RuntimeError(self.disabled)
                # Wait as long as it asks, or longer each time
                try:
                    delay = float(error.headers.get('Retry-After', ''))
                except (TypeError, ValueError, AttributeError):
                    delay = 5.0 * (attempt + 1)
                print(f'Waiting {min(delay, 60):.0f}s: TheGamesDB asked to slow down', flush=True)
                time.sleep(min(delay, 60))
        if isinstance(reply, dict) and reply.get('remaining_monthly_allowance') == 0:
            self.disabled = "the API key's monthly allowance is used up"
        return reply

    def named_list(self, kind):
        """{id: name} for platforms, publishers, developers or genres, cached a week."""
        if kind not in self.lists:
            cache = os.path.join(tempfile.gettempdir(), f'cbm_tgdb_{kind}.json')
            if os.path.exists(cache) and time.time() - os.path.getmtime(cache) < LIST_CACHE_DAYS * 86400:
                with open(cache, encoding='utf-8') as source:
                    self.lists[kind] = json.load(source)
            else:
                reply = self.get(kind.capitalize())
                items = (reply.get('data') or {}).get(kind) or {}
                values = items.values() if isinstance(items, dict) else items
                self.lists[kind] = {str(item['id']): item.get('name', '') for item in values if 'id' in item}
                with open(cache, 'w', encoding='utf-8') as output:
                    json.dump(self.lists[kind], output)
        return self.lists[kind]

    def platform_ids(self, system):
        names = TGDB_PLATFORM_NAMES.get(system, ())
        return [platform_id for platform_id, name in self.named_list('platforms').items()
                if any(word in name.lower() for word in names)]

    def find(self, system, look):
        """(game, boxart url) for the best match on this system, or (None, None).
        Answers are remembered, so the same game is not asked about again."""
        cache_key = f'{system}|{name_keys(look)[0] if name_keys(look) else look}'
        if cache_key in self.cache:
            entry = self.cache[cache_key]
            return entry.get('game'), entry.get('boxart')
        game, boxart = self.search(system, look)
        self.cache[cache_key] = {'game': game, 'boxart': boxart, 'when': time.time()}
        self.save_cache()
        return game, boxart

    def search(self, system, look):
        platforms = self.platform_ids(system)
        if not platforms:
            return None, None
        reply = self.get('Games/ByGameName', name=base_title(look),
                         fields='players,publishers,genres,overview,developers',
                         include='boxart', **{'filter[platform]': ','.join(platforms)})
        games = (reply.get('data') or {}).get('games') or []
        wanted = name_keys(look)
        best = None
        for game in games:
            if str(game.get('platform')) not in platforms:
                continue
            keys = name_keys(game.get('game_title', ''))
            if wanted and keys and keys[0] == wanted[0]:
                best = game
                break
            if best is None and wanted and keys and difflib.SequenceMatcher(None, wanted[0], keys[0]).ratio() >= 0.9:
                best = game
        if best is None:
            return None, None
        boxart = None
        include = (reply.get('include') or {}).get('boxart') or {}
        base = (include.get('base_url') or {}).get('original', '')
        for picture in (include.get('data') or {}).get(str(best['id']), []):
            if picture.get('side', 'front') == 'front' and picture.get('filename'):
                boxart = base + picture['filename']
                break
        return best, boxart

    def screenshots(self, game_id):
        """{'title': url, 'screen': url} from the game's pictures, remembered too."""
        cache_key = f'images|{game_id}'
        if cache_key in self.cache:
            return self.cache[cache_key].get('found', {})
        found = self.fetch_screenshots(game_id)
        self.cache[cache_key] = {'found': found, 'when': time.time()}
        self.save_cache()
        return found

    def fetch_screenshots(self, game_id):
        reply = self.get('Games/Images', games_id=game_id, **{'filter[type]': 'screenshot,titlescreen'})
        data = reply.get('data') or {}
        base = (data.get('base_url') or {}).get('original', '')
        found = {}
        for picture in (data.get('images') or {}).get(str(game_id), []):
            kind = {'titlescreen': 'title', 'screenshot': 'screen'}.get(picture.get('type'))
            if kind and kind not in found and picture.get('filename'):
                found[kind] = base + picture['filename']
        return found

    def details(self, game):
        """The info fields the app's info files hold."""
        def names(kind, ids):
            table = self.named_list(kind)
            return ', '.join(table.get(str(i), '') for i in (ids or []) if table.get(str(i)))
        return {
            'Title': game.get('game_title', ''),
            'Year': (game.get('release_date') or '')[:4],
            'Publisher': names('publishers', game.get('publishers')),
            'Developer': names('developers', game.get('developers')),
            'Genre': names('genres', game.get('genres')),
            'Players': str(game.get('players') or ''),
            'About': (game.get('overview') or '').strip(),
            'Source': f"TheGamesDB, https://thegamesdb.net/game.php?id={game.get('id')}",
        }


# --- Main ------------------------------------------------------------------
NOT_FOUND_FILE = '_not_found.txt'
NOT_FOUND_DAYS = 30


class NotFound:
    """Remembers lookups that found nothing, so later runs skip them for a while."""

    def __init__(self, path, retry):
        self.path = path
        self.entries, self.retry = {}, retry
        self.lock = threading.Lock()        # several games are looked up at once
        if os.path.exists(self.path):
            with open(self.path, encoding='utf-8') as source:
                for line in source:
                    parts = line.rstrip('\r\n').split('\t')
                    if len(parts) == 4:
                        try:
                            self.entries[tuple(parts[:3])] = float(parts[3])
                        except ValueError:
                            pass

    def recent(self, system, game, kind):
        if self.retry:
            return False
        when = self.entries.get((system, game.lower(), kind))
        return when is not None and time.time() - when < NOT_FOUND_DAYS * 86400

    def add(self, system, game, kind):
        with self.lock:
            self.entries[(system, game.lower(), kind)] = time.time()

    def remove(self, system, game, kind):
        with self.lock:
            self.entries.pop((system, game.lower(), kind), None)

    def save(self):
        with self.lock:
            rows = sorted(self.entries.items())
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, 'w', encoding='utf-8') as output:
            for (system, game, kind), when in rows:
                output.write(f'{system}\t{game}\t{kind}\t{when:.0f}\n')


def image_place(images_root, folder_name, game, flat):
    """(folder, file name without extension) for one image of a game.

    The system's own images folder has Case, Title and Screen subfolders. A folder
    chosen by the user is used as it is: case images take the game's name and the
    others add ' - Title' or ' - Screen'.
    """
    if flat:
        return images_root, game if folder_name == 'Case' else f'{game} - {folder_name}'
    return os.path.join(images_root, folder_name), game


def has_image(folder, game):
    return any(os.path.exists(os.path.join(folder, game + ext)) for ext in IMAGE_EXTENSIONS)


def run(jobs, kinds, want_info, images_root, info_root, retry=False, flat=False, not_found_path=None):
    collections = {}
    counts = {'images': 0, 'info': 0, 'filled': 0, 'missing': 0, 'skipped': 0}
    not_found = NotFound(not_found_path or os.path.join(info_root, NOT_FOUND_FILE), retry)
    # Several games are looked up at once (DOWNLOAD_WORKERS); what they share is
    # changed under this lock, and TheGamesDB is asked one request at a time
    lock = threading.RLock()
    tgdb_lock = threading.Lock()
    wiki_turns = threading.Semaphore(WIKI_AT_ONCE)

    def say(*parts, **options):
        with lock:
            print(*parts, **options)

    def bump(name):
        with lock:
            counts[name] += 1

    def collection(system, remote):
        with lock:
            if (system, remote) not in collections:
                collections[(system, remote)] = collection_for(system, remote)
            return collections[(system, remote)]

    def every_title(system):
        """All three image lists of a system as one, to match names against."""
        with lock:
            if (system, '*') not in collections:
                names = []
                for _, remote in IMAGE_TYPES.values():
                    names += collection(system, remote).names
                collections[(system, '*')] = Collection(sorted(set(names)))
            return collections[(system, '*')]

    def wikipedia(look, system):
        with wiki_turns:
            return wikipedia_info(look, system)

    plus4world_state = {}
    czone_state = {'site': None, 'failures': 0}
    tgdb_key = os.environ.get('CBM_TGDB_KEY', '').strip()
    tgdb = TheGamesDB(tgdb_key) if tgdb_key else None
    tgdb_warned = set()

    def tgdb_lookup(system, game, look):
        """(game, boxart url) from TheGamesDB, or (None, None). Problems are reported once."""
        if tgdb is None or tgdb.disabled:
            return None, None
        try:
            with tgdb_lock:
                return tgdb.find(system, look)
        except Exception as error:
            reason = str(error)
            with lock:
                if reason not in tgdb_warned:
                    tgdb_warned.add(reason)
                    print(f'Warning: TheGamesDB: {reason}', flush=True)
            return None, None

    def plus4world_list():
        """The Plus/4 World tape list, or None if it cannot be had (asked once a run)."""
        with lock:
            if 'index' not in plus4world_state:
                try:
                    plus4world_state['index'] = plus4world_index()
                except Exception as error:
                    plus4world_state['index'] = None
                    print(f'Warning: Plus/4 World: {error}', flush=True)
            return plus4world_state['index']

    def czone_lookup(system, look):
        """The Commodore Zone's entry for a C64 game, or None. After three failed
        requests in a row the site is left alone for the rest of the run."""
        if system != 'C64' or czone_state['failures'] >= 3:
            return None
        with lock:
            if czone_state['site'] is None:
                czone_state['site'] = CommodoreZone(say)
        try:
            entry = czone_state['site'].find(look)
            with lock:
                czone_state['failures'] = 0
            return entry
        except Exception as error:
            with lock:
                czone_state['failures'] += 1
                if czone_state['failures'] == 3:
                    print(f'Warning: The Commodore Zone could not be reached ({error}), so it was skipped', flush=True)
            return None

    def process(number, system, game, chosen):
        say(f'Progress: {number}/{len(jobs)} {game}', flush=True)
        # Files keep the game's own name; matching uses the tidied one, or the
        # title picked for it from a list of maybes ('-' if none was right)
        look = chosen if chosen and chosen != '-' else tidy_title(game)
        fallback = {}
        p4_info, p4_images = None, {}
        p4_row = None
        if system == 'C16':
            index = plus4world_list()
            p4_row = index.find(game, look) if index else None
            if p4_row:
                try:
                    p4_info, p4_images = plus4world_details(p4_row)
                    time.sleep(PLUS4WORLD_PAUSE)
                except Exception as error:
                    say(f'Warning: {game} Plus/4 World: {error}', flush=True)
                    p4_info = {'Title': p4_row['title'], 'Year': p4_row['year'], 'Memory': p4_row['memory']}
        try:
            titles = every_title(system)
            known = titles.find(look)
            if known:
                year, publisher = tosec_details(known)
                fallback = {'Year': year, 'Publisher': publisher}
            elif chosen != '-' and not p4_row:
                options = titles.maybes(look)
                if system == 'C16' and plus4world_list():
                    options = list(dict.fromkeys(plus4world_list().collection.maybes(look) + options))[:8]
                if options:
                    say('Maybe: ' + '\t'.join([game] + options), flush=True)
        except Exception as error:
            with lock:
                collections[(system, '*')] = Collection([])     # do not try again for every game
            say(f'Warning: {game}: {error}', flush=True)
        # The Commodore Zone (C64) is asked before TheGamesDB, once per game
        cz = {}

        def cz_entry():
            if 'entry' not in cz:
                cz['entry'] = czone_lookup(system, look)
            return cz['entry']

        # TheGamesDB is asked only if something is still missing, once per game
        tg = {}

        def tg_game():
            if 'game' not in tg:
                tg['game'], tg['boxart'] = tgdb_lookup(system, game, look)
            return tg['game']

        def tg_picture(kind):
            if tg_game() is None:
                return None
            if kind == 'case':
                return tg.get('boxart')
            if 'shots' not in tg:
                try:
                    with tgdb_lock:
                        tg['shots'] = tgdb.screenshots(tg['game']['id'])
                except Exception as error:
                    tg['shots'] = {}
                    say(f'Warning: {game} TheGamesDB pictures: {error}', flush=True)
            return tg['shots'].get(kind)

        def save_picture(url, target, file_name):
            data = fetch(url, binary=True)
            extension = os.path.splitext(urllib.parse.urlparse(url).path)[1].lower() or '.jpg'
            os.makedirs(target, exist_ok=True)
            with open(os.path.join(target, file_name + extension), 'wb') as output:
                output.write(data)

        for kind in kinds:
            folder_name, remote = IMAGE_TYPES[kind]
            target, file_name = image_place(images_root, folder_name, game, flat)
            if has_image(target, file_name):
                not_found.remove(system, game, kind)
                continue
            if not_found.recent(system, game, kind):
                bump('skipped')
                continue
            if p4_images.get(kind):
                try:
                    data = fetch(p4_images[kind], binary=True)
                    extension = os.path.splitext(urllib.parse.urlparse(p4_images[kind]).path)[1].lower() or '.gif'
                    os.makedirs(target, exist_ok=True)
                    with open(os.path.join(target, file_name + extension), 'wb') as output:
                        output.write(data)
                    bump('images')
                    time.sleep(PLUS4WORLD_PAUSE)
                    continue
                except Exception as error:
                    say(f'Warning: {game} {folder_name} from Plus/4 World: {error}', flush=True)
            try:
                match = collection(system, remote).find(look)
                if not match and kind == 'screen' and cz_entry() and cz_entry().get('picture'):
                    try:
                        save_picture(cz_entry()['picture'], target, file_name)
                        bump('images')
                        time.sleep(CZONE_PAUSE)
                        continue
                    except Exception as error:
                        say(f'Warning: {game} Screen from The Commodore Zone: {error}', flush=True)
                if not match:
                    url = tg_picture(kind)
                    if url:
                        save_picture(url, target, file_name)
                        bump('images')
                        continue
                    bump('missing')
                    if not (tgdb and tgdb.disabled):     # a key problem is not a real miss
                        not_found.add(system, game, kind)
                    continue
                year, publisher = tosec_details(match)
                if not fallback.get('Year'):
                    fallback = {'Year': year, 'Publisher': publisher}
                data = download_image(system, remote, match)
                os.makedirs(target, exist_ok=True)
                with open(os.path.join(target, file_name + '.png'), 'wb') as output:
                    output.write(data)
                bump('images')
            except Exception as error:
                say(f'Warning: {game} {folder_name}: {error}', flush=True)

        if want_info:
            target = os.path.join(info_root, game + '.txt')
            existing = os.path.exists(target)
            gaps = set()
            if existing:
                not_found.remove(system, game, 'info')
                try:
                    gaps = info_gaps(target)
                except OSError as error:
                    say(f'Warning: {game} info: {error}', flush=True)
                    return
                if not gaps:
                    not_found.remove(system, game, 'fill')
                    return
                if not_found.recent(system, game, 'fill'):
                    bump('skipped')
                    return
            elif not_found.recent(system, game, 'info'):
                bump('skipped')
                return
            try:
                if not existing and p4_info and p4_info.get('Publisher'):
                    layers = [p4_info]      # Plus/4 World had the game's page: no need for others
                else:
                    # Plus/4 World (C16) or The Commodore Zone (C64) first, then
                    # TheGamesDB, then Wikipedia, field by field. When filling an
                    # existing file, the later ones are asked only for what is
                    # still missing.
                    layers = [p4_info or {}]
                    if cz_entry():
                        layers.append(czone_info(cz_entry()))

                    def still_missing(wanted):
                        have = {k for layer in layers for k, v in layer.items() if v}
                        return set(wanted) - have

                    if existing:
                        # Free sources first; TheGamesDB (the key's monthly
                        # allowance) only for what they could not supply
                        if fallback:        # year and publisher from the picture lists' names
                            layers.append({k: v for k, v in fallback.items() if v})
                        if still_missing(gaps):
                            layers.append(wikipedia(look, system) or {})
                        if still_missing(gaps) and tg_game() is not None:
                            with tgdb_lock:
                                layers.append(tgdb.details(tg['game']))
                    else:
                        if still_missing({'Year', 'Publisher', 'Genre'}) and tg_game() is not None:
                            with tgdb_lock:
                                layers.append(tgdb.details(tg['game']))
                        if not any(layer.get('About') for layer in layers):
                            layers.append(wikipedia(look, system) or {})
                        if fallback:
                            layers.append({k: v for k, v in fallback.items() if v})
                info = {}
                for layer in reversed(layers):
                    info.update({k: v for k, v in layer.items() if v})
                info['Sources'] = [layer['Source'] for layer in layers if layer.get('Source')]
            except Exception as error:
                say(f'Warning: {game} info: {error}', flush=True)
                return        # a network problem is not a miss, so try again next run
            if existing:
                filled = fill_info(target, info, [layer['Source'] for layer in layers if layer.get('Source') and
                                                   any(layer.get(field) for field in GAP_FIELDS + EXTRA_FIELDS + ('About',))])
                if filled:
                    bump('filled')
                    say(f'Filled: {game}: {", ".join(filled)}', flush=True)
                if filled and not info_gaps(target):
                    not_found.remove(system, game, 'fill')
                elif not (tgdb and tgdb.disabled):     # nothing more to be had for now
                    not_found.add(system, game, 'fill')
            elif any(v for k, v in info.items() if k not in ('Source', 'Sources')):
                write_info(target, look, system, info, {})
                bump('info')
            else:
                bump('missing')
                if not (tgdb and tgdb.disabled):     # a TheGamesDB problem is not a real miss
                    not_found.add(system, game, 'info')
            time.sleep(0.1)   # be polite to Wikipedia
        if number % 25 == 0:
            # Each has its own lock: holding the shared one here could wait on a
            # game that is reading the Commodore Zone list and wants to print
            not_found.save()
            site = czone_state['site']
            if site:
                site.save()

    def safely(job):
        number, (system, game, chosen) = job
        try:
            process(number, system, game, chosen)
        except Exception as error:      # one game's problem must not stop the rest
            say(f'Warning: {game}: {error}', flush=True)

    if len(jobs) <= 1 or DOWNLOAD_WORKERS <= 1:
        for job in enumerate(jobs, 1):
            safely(job)
    else:
        with concurrent.futures.ThreadPoolExecutor(max_workers=DOWNLOAD_WORKERS) as pool:
            list(pool.map(safely, enumerate(jobs, 1)))
    not_found.save()
    if czone_state['site']:
        czone_state['site'].save()
    print(f'Done: {counts["images"]} images, {counts["info"]} info files, {counts["filled"]} filled in, '
          f'{counts["missing"]} not found, {counts["skipped"]} skipped as not found before', flush=True)


# --- Matching manuals to games ---------------------------------------------
MANUAL_EXTENSIONS = {'.txt', '.pdf', '.htm', '.html', '.rtf', '.doc', '.docx', '.odt', '.epub',
                     '.djvu', '.xps', '.jpg', '.jpeg', '.png', '.gif', '.bmp'}


def match_manuals(folder, games):
    """Rows for each manual file under folder: (path, tags, 'exact', game) or
    (path, tags, 'maybe', game, game...) or (path, tags, 'none'). The title is
    what comes before the brackets ("Ace of Aces (Europe) (Quick Reference)" is
    Ace of Aces); the brackets are the tags, kept so a game can have several."""
    labels = sorted(set(games))
    collection = Collection(labels)
    by_title = {}
    for label in labels:
        by_title.setdefault(base_title(label), label)
    rows = []
    for root, _, files in os.walk(folder):
        for name in sorted(files):
            stem, extension = os.path.splitext(name)
            if extension.lower() not in MANUAL_EXTENSIONS:
                continue        # the scans' companion .zip and .json files, and anything else
            title = base_title(stem)
            tags = stem[len(title):].strip() if stem.startswith(title) else ''
            look = tidy_title(title)
            path = os.path.join(root, name)
            match = collection.find(look)
            if match:
                rows.append((path, tags, 'exact', match))
                continue
            options = [by_title.get(option, option) for option in collection.maybes(look)]
            rows.append((path, tags, 'maybe', *options) if options else (path, tags, 'none'))
    return rows


def missing_games(system, have_names):
    """Titles in the system's collections that match none of the games we have.

    Returns sorted (title, year, publisher) rows. Only names are compared and
    nothing is downloaded apart from the collections' file lists.
    """
    have = set()
    for name in have_names:
        have.update(name_keys(name))
    titles = {}
    for folder in ('Named_Boxarts', 'Named_Titles'):
        for name in collection_for(system, folder).names:
            keys = name_keys(name)
            if not keys or any(key in have for key in keys):
                continue
            year, publisher = tosec_details(name)
            current = titles.get(keys[0])
            if current is None or (year and not current[1]):
                titles[keys[0]] = (base_title(name), year or '', publisher or '')
    return sorted(titles.values(), key=lambda row: row[0].lower())


def main(arguments=None):
    sys.stdout.reconfigure(errors='replace')
    parser = argparse.ArgumentParser(description='Download artwork and information for Commodore games')
    parser.add_argument('--jobs', help='file of system<TAB>game lines')
    parser.add_argument('--missing', metavar='SYSTEM', help='list known games for SYSTEM that --jobs lacks')
    parser.add_argument('--out', help='where --missing writes its list')
    parser.add_argument('--images', default='', help='comma separated: case,title,screen')
    parser.add_argument('--info', action='store_true', help='also fetch game information')
    parser.add_argument('--retry', action='store_true', help='try again for games not found before')
    parser.add_argument('--flat-images', action='store_true',
                        help='images root is a chosen folder: no Case/Title/Screen subfolders')
    parser.add_argument('--not-found', help='file that remembers lookups that found nothing')
    parser.add_argument('--images-root')
    parser.add_argument('--info-root')
    parser.add_argument('--match-manuals', metavar='FOLDER',
                        help='match the manual files in FOLDER to the --jobs games, writing --out')
    options = parser.parse_args(arguments)
    kinds = [k for k in options.images.split(',') if k in IMAGE_TYPES]
    jobs = []
    with open(options.jobs or os.devnull, encoding='utf-8-sig') as source:
        for line in source:
            system, game, chosen = (line.rstrip('\r\n').split('\t') + ['', ''])[:3]
            if system.upper() in SYSTEMS and game:
                jobs.append((system.upper(), game, chosen))
    if options.match_manuals:
        if not options.out:
            print('Error: --match-manuals needs --out', flush=True)
            return 1
        rows = match_manuals(options.match_manuals, [game for _, game, _ in jobs])
        with open(options.out, 'w', encoding='utf-8') as output:
            output.write(''.join('\t'.join(row) + '\n' for row in rows))
        print(f"Done: {sum(row[2] == 'exact' for row in rows)} matched, "
              f"{sum(row[2] == 'maybe' for row in rows)} to choose, "
              f"{sum(row[2] == 'none' for row in rows)} not matched", flush=True)
        return 0
    if options.missing:
        system = options.missing.upper()
        if system not in SYSTEMS or not options.out:
            print('Error: --missing needs C64, VIC20 or C16, and --out', flush=True)
            return 1
        try:
            rows = missing_games(system, [game for job_system, game, _ in jobs if job_system == system])
        except Exception as error:
            print(f'Error: {error}', flush=True)
            return 1
        with open(options.out, 'w', encoding='utf-8') as output:
            output.write(''.join(f'{title}\t{year}\t{publisher}\n' for title, year, publisher in rows))
        print(f'Done: {len(rows)} missing', flush=True)
        return 0
    if not (options.images_root and options.info_root):
        print('Error: --images-root and --info-root are needed for downloads', flush=True)
        return 1
    try:
        run(jobs, kinds, options.info, options.images_root, options.info_root, options.retry,
            options.flat_images, options.not_found)
    except Exception as error:
        print(f'Error: {error}', flush=True)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
