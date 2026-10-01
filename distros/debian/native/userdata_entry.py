"""Protected observer entry only. No arbitrary source/destination command line."""
from pathlib import Path
import os
import stat
import sys
here=Path(__file__).resolve().parent
for p in (here, *here.parents):
    s=p.stat()
    if s.st_uid!=0 or s.st_mode & 0o022: raise ValueError('UserDataToolPlacement')
sys.path.insert(0,str(here))
from userdata_producer import observe, reopen, decode, canonical
if sys.argv[1:] not in (['--observe'],['--reopen']): raise ValueError('UserDataObserverAction')
raw=sys.stdin.buffer.read(1024*1024+1)
if len(raw)>1024*1024: raise ValueError('UserDataObserverSize')
try: print(canonical((observe if sys.argv[1]=='--observe' else reopen)(decode(raw))).decode(),flush=True)
except (OSError,ValueError,KeyError,TypeError): sys.exit(2)
