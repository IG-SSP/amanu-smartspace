"""Offline Amanu -> existing tracker-sync proposals. Never publishes or reads credentials."""
import argparse
import hashlib
import json
import re
from pathlib import Path

HEADERS = ["Объект", "Задача", "Ответственный", "Срок", "Цитата", "Таймкод"]


def normalize(text):
    return " ".join(text.split()).casefold()


def extract(session, queue):
    session = Path(session).resolve()
    transcript_bytes = (session / "transcript.json").read_bytes()
    transcript = json.loads(transcript_bytes)
    summary_bytes = (session / "summary.md").read_bytes()
    summary = summary_bytes.decode("utf-8-sig")
    rows, table, seen = [], False, set()
    for line in summary.splitlines():
        if not line.lstrip().startswith("|"):
            table = False
            continue
        cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
        if cells == HEADERS:
            table = True
            continue
        if not table or len(cells) != 6 or all(re.fullmatch(r"[: -]+", c) for c in cells):
            continue
        obj, task, owner, deadline, quote, timestamp = cells
        quote = quote.strip('«»"“”')
        match = re.fullmatch(r"\[?(?:(\d+):)?(\d{1,2}):(\d{2})\]?", timestamp)
        evidence = None
        if match and len(quote) >= 10 and int(match[2]) < 60 and int(match[3]) < 60:
            ms = (int(match[1] or 0) * 3600 + int(match[2]) * 60 + int(match[3])) * 1000
            # Permit displayed seconds to be rounded; the quote must belong to that utterance.
            evidence = next((s for s in transcript.get("segments", [])
                             if normalize(quote) in normalize(s.get("text", ""))
                             and s["start_ms"] - 1000 <= ms <= s["end_ms"] + 1000), None)
        # Stable across repeated summaries of the same meeting; transcript revision recorded separately.
        key = hashlib.sha256((str(session) + "\0" + normalize(task) + "\0" + normalize(quote)).encode()).hexdigest()
        if key in seen:
            continue
        seen.add(key)
        description = (f"Объект: {obj}\nОтветственный по протоколу (не проверен): {owner}\n"
                       f"Срок по протоколу (не проверен): {deadline}\n\n"
                       f"Основание [{timestamp}]: «{quote}»\n\n"
                       f"Источник: {session / 'transcript.md'}\nAmanu draft: {key}")
        rows.append({"idempotency_key": key, "evidence_verified": evidence is not None,
                     "speaker": evidence.get("speaker") if evidence else None,
                     "proposal": {"queue": queue, "summary": task, "description": description, "markupType": "md"}})
    return {"schema_version": 1, "status": "draft", "session": str(session),
            "transcript_sha256": hashlib.sha256(transcript_bytes).hexdigest(),
            "summary_sha256": hashlib.sha256(summary_bytes).hexdigest(), "tasks": rows}


def write_drafts(session, queue):
    session = Path(session).resolve()
    data = extract(session, queue)
    output = session / "tracker-drafts.json"
    # Replace draft only; never overwrite approval/application state in another file.
    temporary = output.with_suffix(".json.tmp")
    temporary.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(output)
    proposals = session / "tracker-proposals"
    proposals.mkdir(exist_ok=True)
    # Revision namespace prevents stale proposals from masquerading as current ones.
    revision = proposals / data["summary_sha256"][:16]
    revision.mkdir(exist_ok=True)
    exported = 0
    for row in data["tasks"]:
        if row["evidence_verified"]:
            path = revision / (row["idempotency_key"] + ".json")
            path.write_text(json.dumps(row["proposal"], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            exported += 1
    return {"drafts": str(output), "current_proposals": str(revision), "exported": exported,
            "unverified": len(data["tasks"]) - exported, "publishing": "preview-and-confirm"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True)
    parser.add_argument("--queue", required=True, help="Chosen Tracker queue; no guessed destination")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Z][A-Z0-9_]{1,63}", args.queue):
        parser.error("invalid queue")
    print(json.dumps(write_drafts(args.session, args.queue), ensure_ascii=False))


if __name__ == "__main__":
    main()
