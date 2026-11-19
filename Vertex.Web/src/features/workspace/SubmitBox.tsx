import {useState} from 'react'
import {useSession} from '../../app/session'
import {Avatar} from '../../components/Avatar'
import {Icon} from '../../components/Icon'
import {plural} from '../../lib/format'
import type {ChangedFile} from '../../mocks/sampleData'

/** Describes and submits the changed files as one revision. Disabled until the app can open workspaces. */
export function SubmitBox({files}: {files: ChangedFile[]}) {
    const {session, branch} = useSession()
    const [summary, setSummary] = useState('')
    const added = files.reduce((sum, f) => sum + f.add, 0)
    const deleted = files.reduce((sum, f) => sum + f.del, 0)

    return (
        <div className="commit-box">
            <div className="commit-author">
                <Avatar person={{name: session?.username ?? '?'}}/>
                <span>
                    <b>Submit to {branch?.name ?? 'branch'}</b>
                    <small>{plural(files.length, 'file')}, +{added} −{deleted}</small>
                </span>
            </div>
            <input value={summary} onChange={e => setSummary(e.target.value)} placeholder="Summary (required)"/>
            <textarea placeholder="Description" rows={2}/>
            <button type="button" disabled title="Coming with workspace support. Until then, run 'vx submit'.">
                <Icon name="commit" size={15}/>
                Submit {plural(files.length, 'file')}
            </button>
        </div>
    )
}
