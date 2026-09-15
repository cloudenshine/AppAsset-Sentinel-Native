"""Safe counterexamples for reviewed logic, NOT execution of the C# app.
Only fresh temporary fixtures are created/deleted. Requires Python 3.10+ and Node.js.
Does not access registry, user settings, application data, or the network.
"""
from __future__ import annotations
import hashlib
import json
import ntpath
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

BASELINE = 'f7c9dbf7a29b55cf7fa3d832bf3833ac67ee6e75'

def run() -> dict:
    results: list[dict] = []
    source = r'C:\Users\Shine\.ollama\models'
    requested = r'D:\AIStack_Vault\models\ollama'
    actual = ntpath.join(ntpath.dirname(requested), ntpath.basename(source))
    assert actual != requested
    results.append(dict(id='requested_target_leaf_ignored', source=source, requested=requested,
                        actual_by_reviewed_composition=actual, defect_observed=True))

    for case, anchor_time, vault_time in [('equal_timestamp_different_content',1000,1000),
                                         ('newer_anchor_overwrites_vault',2000,1000)]:
        with tempfile.TemporaryDirectory(prefix='sentinel_logic_probe_') as tmp:
            root = Path(tmp)
            anchor, vault = root/'anchor', root/'vault'
            anchor.mkdir(); vault.mkdir()
            a, v = anchor/'model.bin', vault/'model.bin'
            a.write_bytes(b'UNIQUE-ANCHOR'); v.write_bytes(b'UNIQUE-VAULT!')
            os.utime(a, (anchor_time, anchor_time)); os.utime(v, (vault_time, vault_time))
            originals = {'anchor': a.read_bytes(), 'vault': v.read_bytes()}
            # Mirrors only the file merge + deletion predicate of AutoHealDrift.
            for file in list(anchor.rglob('*')):
                if not file.is_file():
                    continue
                dest = vault/file.relative_to(anchor)
                dest.parent.mkdir(parents=True, exist_ok=True)
                if not dest.exists() or file.stat().st_mtime > dest.stat().st_mtime:
                    shutil.copyfile(file, dest)
            shutil.rmtree(anchor)
            retained = [p.read_bytes() for p in root.rglob('*') if p.is_file()]
            lost = [name for name,data in originals.items() if data not in retained]
            assert lost == (['anchor'] if anchor_time == vault_time else ['vault'])
            results.append(dict(id=case, lost_unique_version=lost,
                                remaining_file=v.read_text(), defect_observed=True))

    original, corrupt = b'AAAA', b'BBBB'
    accepted = len(corrupt) >= len(original)
    assert accepted and hashlib.sha256(original).digest() != hashlib.sha256(corrupt).digest()
    results.append(dict(id='total_bytes_is_not_integrity', predicate_accepts=accepted,
                        contents_equal=False, defect_observed=True))

    # Execute only a fixed harmless JS handler string, reproducing raw Windows-path
    # interpolation into an inline event handler. This is not a malicious payload.
    js = r'''
const original = String.raw`C:\Tools\ComfyUI\models`;
const handler = `openRelocateModal('${original}')`;
let captured;
new Function('openRelocateModal', handler)((p) => captured = p);
console.log(JSON.stringify({id:'inline_handler_path_corruption', original, handler,
    received:captured, defect_observed:captured !== original}));
'''
    proc = subprocess.run(['node','-e',js], check=True, text=True, capture_output=True, timeout=10)
    outcome = json.loads(proc.stdout)
    assert outcome['defect_observed'] is True
    results.append(outcome)

    # Keep a link only when its reverse isn't reachable: same cycle-pruning logic.
    edges = [('A','B'), ('B','C'), ('C','A')]
    adj: dict[str, set[str]] = {}
    def reachable(start: str, end: str, seen: set[str]) -> bool:
        if start == end: return True
        if start in seen: return False
        seen.add(start)
        return any(reachable(n,end,seen) for n in adj.get(start,set()))
    kept = []
    for u,v in edges:
        if reachable(v,u,set()): continue
        adj.setdefault(u,set()).add(v); kept.append((u,v))
    removed = [e for e in edges if e not in kept]
    assert removed == [('C','A')]
    results.append(dict(id='cycle_pruning_loses_dependency', original_edges=edges,
                        remaining_edges=kept, removed_edges=removed, defect_observed=True))
    return dict(baseline_commit=BASELINE, verification_type='isolated_logic_counterexamples',
                native_windows_execution=False, csharp_app_execution=False,
                note='These probes demonstrate the reviewed predicates; they are not native acceptance tests.',
                counterexamples=len(results), results=results)

if __name__ == '__main__':
    output = run()
    target = Path(__file__).with_name('logic_probe_results.json')
    target.write_text(json.dumps(output, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(target.read_text(encoding='utf-8'))
