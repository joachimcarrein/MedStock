"""Generate SharedResource.resx (en) and SharedResource.nl.resx from strings.tsv.

strings.tsv: one line per key: key<TAB>english<TAB>dutch. Lines starting with # are comments.
Run: python tools/gen_resx.py
"""
import pathlib
from xml.sax.saxutils import escape

root = pathlib.Path(__file__).resolve().parent.parent / "medstock/src/MedStock.Web/Resources"
rows = []
for n, line in enumerate((root / "strings.tsv").read_text(encoding="utf-8").splitlines(), 1):
    if not line.strip() or line.startswith("#"):
        continue
    parts = line.split("\t")
    if len(parts) != 3:
        raise SystemExit(f"strings.tsv line {n}: expected 3 tab-separated columns, got {len(parts)}")
    rows.append(parts)
keys = [r[0] for r in rows]
dups = {k for k in keys if keys.count(k) > 1}
if dups:
    raise SystemExit(f"duplicate keys: {sorted(dups)}")

HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
"""
for col, name in ((1, "SharedResource.resx"), (2, "SharedResource.nl.resx")):
    out = [HEADER]
    for r in rows:
        out.append(f'  <data name="{escape(r[0])}" xml:space="preserve"><value>{escape(r[col])}</value></data>\n')
    out.append("</root>\n")
    (root / name).write_text("".join(out), encoding="utf-8", newline="\n")
print(f"{len(rows)} keys written")
