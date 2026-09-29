"""Add static desktop strings to the English language-pack template."""
import hashlib
import html
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parents[1]
catalog_path = root / 'src/Wyrmwatch.Desktop/Assets/Languages/en.json'
catalog = json.loads(catalog_path.read_text(encoding='utf-8')) if catalog_path.exists() else {'Id': 'en', 'Name': 'English', 'Strings': {}}

def key(text):
    name = 'Ui.' + hashlib.sha256(text.encode('utf-8')).hexdigest()[:12]
    catalog['Strings'][name] = text
    return name

def convert(match):
    text = html.unescape(match[2])
    if text.startswith('{') or len(text) < 2 or not any(c.isalpha() for c in text) or text in ('Wyrmwatch', 'DRAGONWILDS'):
        return match[0]
    return f'{match[1]}="{{DynamicResource {key(text)}}}"'

view = root / 'src/Wyrmwatch.Desktop/MainWindow.axaml'
view.write_text(re.sub(r'\b(Text|Content|PlaceholderText)="([^"\n]*)"', convert, view.read_text(encoding='utf-8')), encoding='utf-8')
code = (root / 'src/Wyrmwatch.Desktop/MainWindow.axaml.cs').read_text(encoding='utf-8')
for block in re.findall(r'string\[\] (?:titles|descriptions) = \[(.*?)\];', code):
    for text in re.findall(r'"([^"]+)"', block):
        key(text)
catalog_path.parent.mkdir(parents=True, exist_ok=True)
catalog_path.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'{len(catalog["Strings"])} interface strings in the English template.')
