"""Checks the language files.

en.json has to be clean. Any other file in the folder is listed with how far behind it is. With --all it must have
the same keys as en.json and the same placeholders ({0}, {1:0}...) in each text.

usage: python tools/check-languages.py [--all]"""
import json
import os
import re
import sys

LANG_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "src", "CHC.Core", "Localization", "Languages")
PLACEHOLDER = re.compile(r"\{\d+(?::[^}]*)?\}")


def load(code):
    with open(os.path.join(LANG_DIR, code + ".json"), encoding="utf-8") as f:
        return json.load(f)


def stray_braces(code, texts):
    return [f"{code}: stray brace in {key}" for key, value in texts.items() if "{" in PLACEHOLDER.sub("", value) or "}" in PLACEHOLDER.sub("", value)]


def compare(code, texts, en):
    problems = []
    for key, value in en.items():
        if key.startswith("_"):
            continue
        if key not in texts:
            problems.append(f"{code}: missing {key}")
        elif sorted(set(PLACEHOLDER.findall(value))) != sorted(set(PLACEHOLDER.findall(texts[key]))):
            problems.append(f"{code}: {key} has {sorted(set(PLACEHOLDER.findall(texts[key])))}, en.json {sorted(set(PLACEHOLDER.findall(value)))}")
    problems += [f"{code}: extra {key}" for key in texts if key not in en]
    return problems + stray_braces(code, texts)


def main():
    strict = "--all" in sys.argv[1:]
    en = load("en")
    codes = sorted(f[:-5] for f in os.listdir(LANG_DIR) if f.endswith(".json") and f != "en.json")
    problems = stray_braces("en", en)
    if strict:
        for code in codes:
            problems += compare(code, load(code), en)
        print("\n".join(problems) if problems else f"{len(codes) + 1} languages OK, {len(en)} keys")
        return 1 if problems else 0

    print("\n".join(problems) if problems else f"en.json OK, {len(en)} keys")
    for code in codes:
        behind = compare(code, load(code), en)
        missing = sum(1 for p in behind if ": missing " in p)
        print(f"  {code}: {missing} texts behind" + (f", {len(behind) - missing} other differences" if len(behind) > missing else ""))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
