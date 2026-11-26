import {type Change, useWorkspace} from '../../app/workspace'
import {Icon} from '../../components/Icon'
import {Modal} from '../../components/ui'
import {fileName, plural} from '../../lib/format'

/** Confirms discarding changes: the one step in the Changes page that can lose work. */
export function RevertDialog({paths, changes, onClose}: {paths: string[]; changes: readonly Change[]; onClose: () => void}) {
    const {revert} = useWorkspace()
    const affected = changes.filter(c => paths.some(p => c.path === p || c.path.startsWith(`${p}/`) || c.previousPath === p))
    const added = affected.filter(c => c.kind === 'Added').length
    const restored = affected.length - added
    const single = affected.length === 1 ? fileName(affected[0].path) : undefined

    return (
        <Modal title={single ? `Revert ${single}?` : `Revert ${plural(affected.length, 'change')}?`} onClose={onClose} footer={
            <>
                <button type="button" className="button" onClick={onClose} autoFocus>Cancel</button>
                <button type="button" className="button danger solid" disabled={restored === 0} onClick={() => {
                    onClose()
                    void revert(paths)
                }}>
                    <Icon name="undo" size={14}/>
                    Discard {restored === 1 ? 'change' : 'changes'}
                </button>
            </>
        }>
            <p>
                {restored > 0
                    ? <>{plural(restored, 'file')} get the content they had at your last submit or sync back from the server, and their locks are released. This can't be undone.</>
                    : <>There's nothing to restore.</>}
            </p>
            {added > 0 && <p className="muted">{plural(added, 'new file')} {added === 1 ? 'stays' : 'stay'} as {added === 1 ? 'it is' : 'they are'}: the server has no earlier version. Delete {added === 1 ? 'it' : 'them'}, or add {added === 1 ? 'it' : 'them'} to .vxignore.</p>}
        </Modal>
    )
}
