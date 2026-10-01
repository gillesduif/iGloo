"""Read-only adapters for a nominated canonical payload and target account.

The caller supplies the already acquired canonical view. These observations do
not turn arbitrary paths/FDs or a deserialized delivery record into authority.
Only the selected source prefix and destination home use the small tree limits;
the Debian baseline remains the responsibility of the whole-root observer.
"""
import os
import stat

from isolation_policy import canonical, digest, require
from target_files import beneath, mount_id
import userdata_producer as producer
from userdata_contract import declaration, sha


def selected_content(plan, inventory):
    selected = {name: value for name, value in inventory.items() if any(
        name == f['sourceRelativePath'] or name.startswith(f['sourceRelativePath'] + '/') for f in plan['Selection']['folders'])}
    expected = {e['Source']: {k: e[k] for k in ('Kind', 'Length', 'Sha256')} for e in plan['Entries']}
    actual = {name: {k: value[k] for k in ('Kind', 'Length', 'Sha256')} for name, value in selected.items()}
    require(actual == expected, 'UserDataSelectedInventoryChanged')


def content_inventory(inventory):
    # FAT32 does not carry target POSIX ownership. Staging attests path/type/bytes;
    # fresh acquisition additionally pins the mounted source's actual metadata.
    return {name: {k: value[k] for k in ('Kind', 'Length', 'Sha256')} for name, value in inventory.items()}


def observe(payload, target, transfer, authority, delivery):
    p = producer.validate(transfer); d = declaration(authority, transfer)
    require(digest(delivery) == d['DeliverySha256'], 'UserDataDeliveryChanged')
    record = producer.decode(delivery)
    require(set(record) == {'SchemaVersion', 'Origin', 'GenerationId', 'OperationId', 'SourcePrefix',
        'PayloadUuid', 'TransferSha256', 'Inventory', 'PayloadBeforeSha256', 'UnrelatedPayloadAfterSha256',
        'StagingIntentSha256', 'TargetCopySha256', 'StagedTargetSha256'} and
        type(record['SchemaVersion']) is int and record['SchemaVersion'] == 1 and
        record['Origin'] == 'LabAuthoredStagedDocumentsV1' and
        record['GenerationId'] == p['GenerationId'] and record['OperationId'] == p['OperationId'] and
        record['SourcePrefix'] == d['SourcePrefix'] and record['TransferSha256'] == digest(transfer) and
        type(record['PayloadUuid']) is str and record['PayloadUuid'].upper() == d['Bindings'][2]['FileSystemUuid'].upper() and
        all(sha(record[k]) for k in ('PayloadBeforeSha256', 'UnrelatedPayloadAfterSha256', 'StagingIntentSha256',
            'TargetCopySha256', 'StagedTargetSha256')) and
        record['PayloadBeforeSha256'] == record['UnrelatedPayloadAfterSha256'] and
        record['TargetCopySha256'] != record['StagedTargetSha256'], 'UserDataStagingBinding')
    require(os.fstat(payload).st_dev != os.fstat(target).st_dev and
            os.fstatvfs(payload).f_flag & os.ST_RDONLY, 'UserDataCanonicalSourceNotReadOnlyDistinct')
    source = beneath(payload, d['SourcePrefix'], os.O_RDONLY | os.O_DIRECTORY)
    home = None
    try:
        home = beneath(target, p['Home'].lstrip('/'), os.O_RDONLY | os.O_DIRECTORY)
        require(mount_id(source) == mount_id(payload) and mount_id(home) == mount_id(target), 'UserDataAcquisitionMountChanged')
        h = os.fstat(home)
        require((h.st_uid, h.st_gid, stat.S_IMODE(h.st_mode)) == (p['Uid'], p['Gid'], 0o700) and
                not os.listxattr(home), 'UserDataTargetHomeChanged')
        accounts = producer.account(target, p)
        inventory = producer.snapshot(source); before = producer.snapshot(home, True)
        require(content_inventory(inventory) == record['Inventory'], 'UserDataStagedInventoryChanged')
        selected_content(p, inventory)
        require(not any(producer.key(n.split('/')[0]) in {producer.key(f['name']) for f in p['Selection']['folders']}
            for n in before), 'UserDataDestinationConflict')
        return {'AuthoritySha256': digest(authority), 'DeliverySha256': digest(delivery),
            'PayloadIdentity': producer.identity(payload), 'SourcePrefix': d['SourcePrefix'],
            **producer.context_binding(source, target, home, accounts, inventory, before),
            'SourceBefore': inventory, 'DestinationBefore': before}
    finally:
        os.close(source)
        if home is not None: os.close(home)
