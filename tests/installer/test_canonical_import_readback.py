import json
from pathlib import Path
import tempfile
import unittest

import canonical_import_readback as readback


class ImportJournalReadbackTests(unittest.TestCase):
    def test_reopened_chain_retains_provider_and_exact_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory); plan = 'A' * 64
            provenance = {'GenerationId': 'fixture-generation', 'Scope': 'ConfiguredRootImport'}
            (base / 'plan.sha256').write_text(plan)
            previous = None
            for sequence in range(2):
                value = {'SchemaVersion': 2, 'GenerationId': provenance['GenerationId'], 'Provenance': provenance,
                    'Sequence': sequence, 'PreviousSha256': previous, 'PlanSha256': plan}
                data = json.dumps(value).encode(); previous = readback.digest(data)
                (base / f'{sequence:08d}-{previous}.json').write_bytes(data)
            records, references = readback.chain(base, plan, provenance)
            self.assertEqual(2, len(records)); self.assertEqual(previous, references[-1]['Sha256'])
            path = base / references[-1]['Reference']
            path.write_bytes(path.read_bytes() + b'\n')
            with self.assertRaisesRegex(ValueError, 'JournalBindingChanged'):
                readback.chain(base, plan, provenance)

    def test_rehashed_provider_downgrade_still_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory); plan = 'A' * 64
            provenance = {'GenerationId': 'fixture-generation', 'Scope': 'ConfiguredRootImport'}
            (base / 'plan.sha256').write_text(plan)
            data = json.dumps({'SchemaVersion': 2, 'GenerationId': provenance['GenerationId'],
                'Provenance': {**provenance, 'Scope': 'StorageSmoke'}, 'Sequence': 0,
                'PreviousSha256': None, 'PlanSha256': plan}).encode()
            (base / f'00000000-{readback.digest(data)}.json').write_bytes(data)
            with self.assertRaisesRegex(ValueError, 'JournalBindingChanged'):
                readback.chain(base, plan, provenance)

    def test_failed_run_cannot_be_relabelled_by_journal_presence(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            (base / 'canonical-import.stdout').write_text('{}\n{"Session":"OutcomeUnknown"}\n')
            with self.assertRaisesRegex(ValueError, 'RunNotSuccessful'):
                readback.verify(base, base)


if __name__ == '__main__': unittest.main()
