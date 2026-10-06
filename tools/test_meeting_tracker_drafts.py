import json
import tempfile
import unittest
from pathlib import Path
from meeting_tracker_drafts import extract, write_drafts


class DraftTests(unittest.TestCase):
    def test_evidence_idempotence_and_no_guessed_assignee_or_date(self):
        with tempfile.TemporaryDirectory() as directory:
            session = Path(directory)
            (session / "transcript.json").write_text(json.dumps({"segments": [
                {"start_ms": 60000, "end_ms": 65000, "text": "Надо проверить свет завтра.", "speaker": "them"}]}), encoding="utf-8")
            (session / "summary.md").write_text("""## Задачи
| Объект | Задача | Ответственный | Срок | Цитата | Таймкод |
|---|---|---|---|---|---|
| Лодка | Проверить свет | Игорь | завтра | Надо проверить свет завтра. | 01:00 |
| Лодка | Проверить свет | Игорь | завтра | Надо проверить свет завтра. | 01:00 |
| Лодка | Поменять всё | Игорь | сегодня | Выдуманная реплика | 01:00 |
""", encoding="utf-8")
            first = extract(session, "SMARTNEW")
            self.assertEqual(2, len(first["tasks"]))
            self.assertTrue(first["tasks"][0]["evidence_verified"])
            self.assertFalse(first["tasks"][1]["evidence_verified"])
            self.assertNotIn("assignee", first["tasks"][0]["proposal"])
            self.assertNotIn("deadline", first["tasks"][0]["proposal"])
            write_drafts(session, "SMARTNEW")
            before = (session / "tracker-drafts.json").read_bytes()
            write_drafts(session, "SMARTNEW")
            self.assertEqual(before, (session / "tracker-drafts.json").read_bytes())
            self.assertEqual(1, len(list((session / "tracker-proposals").rglob("*.json"))))


if __name__ == "__main__":
    unittest.main()
