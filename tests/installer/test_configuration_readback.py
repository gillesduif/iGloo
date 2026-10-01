"""Synthetic record fixtures; not native configuration evidence."""
import json
from pathlib import Path
import tempfile
import unittest

import canonical_configuration_readback as reader


class ConfigurationReadbackTests(unittest.TestCase):
    def test_console_success_cannot_replace_missing_effects_or_teardown(self):
        with tempfile.TemporaryDirectory() as directory:
            evidence, journals = self.fixture(Path(directory))
            result = reader.verify(evidence, journals)
            self.assertEqual('OutcomeUnknown', result['Configuration'])
            self.assertEqual('NotVerified', result['Teardown'])
            self.assertEqual(['Baseline'], result['CompletedSteps'])
            self.assertFalse(result['ReplayAuthorized'])

    def test_changed_record_reservation_or_observation_rejected(self):
        for mutation in ('record', 'reservation', 'observation', 'operation'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                evidence, journals = self.fixture(Path(directory), mutation)
                with self.assertRaises(ValueError): reader.verify(evidence, journals)

    @staticmethod
    def fixture(base, mutation=None):
        evidence = base/'evidence'; evidence.mkdir(); journals = base/'journals'
        provenance = {'Provider': 'IsolatedFileBackedLab', 'Version': 3, 'Scope': 'CoreConfiguration', 'GenerationId': 'fixture-generation'}
        preflight = {'Phase': 'ConfigurationPreflight', 'Plan': {'OperationId': 'operation', 'Provenance': provenance},
            'PlanSha256': 'A'*64, 'SessionId': 'session', 'Predecessor': {'ResultSha256': 'B'*64}}
        (evidence/'configuration.stdout').write_text(json.dumps(preflight)+'\n'+json.dumps({'Configuration': 'AppliedAndVerified'})+'\n')
        observer = {'OperationId': 'operation', 'PlanSha256': 'A'*64}
        sha = reader.digest(json.dumps(observer, sort_keys=True, separators=(',', ':')).encode())
        for name, records in [('session', [{'Action': 'ConfigureCore', 'State': 'IntentDurable'}]), ('effects', [
                {'Step': 'Baseline', 'Outcome': 'IntentDurable', 'Bindings': [], 'ProtectedStateSha256': 'C'*64},
                {'Step': 'Baseline', 'Outcome': 'AppliedAndVerified', 'Bindings': [], 'ProtectedStateSha256': 'C'*64,
                 'Evidence': {'ObserverEvidence': observer, 'ObserverEvidenceSha256': 'D'*64 if mutation == 'observation' else sha}}])]:
            folder = journals/name/'fixture-generation'; folder.mkdir(parents=True)
            (folder/'plan.sha256').write_text('wrong' if mutation == 'reservation' else preflight['PlanSha256'])
            previous = None
            for index, record in enumerate(records):
                value = {**record, 'SchemaVersion': 3, 'Provenance': provenance, 'GenerationId': 'fixture-generation',
                    'SessionId': 'session', 'PlanSha256': 'A'*64, 'OperationId': 'other' if mutation == 'operation' else 'operation',
                    'Predecessor': preflight['Predecessor'], 'Sequence': index, 'PreviousSha256': previous}
                data = json.dumps(value).encode(); previous = reader.digest(data)
                (folder/f'{index:08d}-{previous}.json').write_bytes(data + (b'\n' if mutation == 'record' else b''))
        return evidence, journals


if __name__ == '__main__': unittest.main()
