import {useState} from 'react'
import {Icon} from '../../components/Icon'
import {type ChangedFile, type ChangeStatus, statusLetter} from '../../mocks/sampleData'
import {SubmitBox} from './SubmitBox'

const allStatuses: ChangeStatus[] = ['modified', 'added', 'deleted']

/** The middle column of the Workspace page: changed files, filterable by name and state, and the submit box. */
export function ChangeList({files, selected, onSelect}: {files: ChangedFile[]; selected: number; onSelect: (index: number) => void}) {
    const [query, setQuery] = useState('')
    const [visible, setVisible] = useState<Set<ChangeStatus>>(new Set(allStatuses))
    const [settings, setSettings] = useState(false)
    const needle = query.trim().toLocaleLowerCase()
    const filtered = files.filter(file => visible.has(file.status) && file.path.toLocaleLowerCase().includes(needle))

    const toggle = (status: ChangeStatus) => {
        setVisible(current => {
            const next = new Set(current)
            if (next.has(status)) next.delete(status)
            else next.add(status)
            return next
        })
    }

    return (
        <section className="changes-pane">
            <div className="pane-tabs">
                <button type="button" className="active">
                    Changes <b>{filtered.length}</b>
                </button>
                <em className="preview-tag" title="The app can't open workspaces yet; these files are made up. 'vx status' shows your real changes.">
                    Preview · sample data
                </em>
            </div>
            <div className="changes-tools">
                <label>
                    <Icon name="search" size={14}/>
                    <input value={query} onChange={event => setQuery(event.target.value)} placeholder="Filter changed files"/>
                </label>
                <div className="change-settings">
                    <button
                        type="button"
                        aria-label="Filter file states"
                        aria-expanded={settings}
                        onClick={() => setSettings(current => !current)}
                        className={settings ? 'active' : ''}
                    >
                        <Icon name="settings" size={14}/>
                    </button>
                    {settings && (
                        <div className="status-menu">
                            <strong>Visible file states</strong>
                            {allStatuses.map(status => (
                                <label key={status}>
                                    <input type="checkbox" checked={visible.has(status)} onChange={() => toggle(status)}/>
                                    <i className={status}/>
                                    {status[0].toUpperCase() + status.slice(1)}
                                </label>
                            ))}
                        </div>
                    )}
                </div>
            </div>
            <div className="change-group">
                <div className="change-group-head">
                    <span>
                        <Icon name="chevron" size={12}/>
                        {files.length} changed files
                    </span>
                </div>
                {filtered.map(file => {
                    const index = files.indexOf(file)
                    const parts = file.path.split('/')
                    const name = parts.pop()
                    return (
                        <button
                            key={file.path}
                            type="button"
                            onClick={() => onSelect(index)}
                            className={`change-file ${selected === index ? 'selected' : ''}`}
                        >
                            <span className={`state ${file.status}`}>{statusLetter[file.status]}</span>
                            <span className="file-name">
                                <b>{name}</b>
                                <small>{parts.join('/') || 'Project root'}</small>
                            </span>
                            <span className="line-stats">
                                <i>+{file.add}</i>
                                <em>−{file.del}</em>
                            </span>
                        </button>
                    )
                })}
            </div>
            <SubmitBox files={files}/>
        </section>
    )
}
