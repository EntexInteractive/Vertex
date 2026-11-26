import {type FormEvent, useState} from 'react'
import {useSession} from '../../app/session'
import {useWorkspace} from '../../app/workspace'
import {Icon} from '../../components/Icon'
import {Banner, Modal} from '../../components/ui'
import {call, host, HostError} from '../../host/client'
import {useHostQuery} from '../../host/useHost'

function useFolderPicker(title: string) {
    const [error, setError] = useState<string>()
    const pick = async (startIn: string): Promise<string | null> => {
        try {
            const picked = await call(host.POST('/api/workspaces/pick-folder', {body: {title, startIn: startIn || null}}))
            return picked.path ?? null
        } catch (e) {
            setError(e instanceof HostError ? e.message : String(e))
            return null
        }
    }
    return {pick, error}
}

function FolderField({label, value, onChange, title, hint}: {label: string; value: string; onChange: (value: string) => void; title: string; hint?: string}) {
    const {pick, error} = useFolderPicker(title)
    return (
        <label className="field">
            <span>{label}</span>
            <div className="field-row">
                <input value={value} onChange={e => onChange(e.target.value)} placeholder="C:\Projects" spellCheck={false}/>
                <button type="button" className="button" onClick={async () => {
                    const folder = await pick(value)
                    if (folder) onChange(folder)
                }}>
                    <Icon name="folder" size={14}/>
                    Browse…
                </button>
            </div>
            {hint && <small>{hint}</small>}
            {error && <small className="field-error">{error}</small>}
        </label>
    )
}

/** Clone: pick a project and a parent folder; a folder named after the project is created inside it. */
export function CloneDialog({onClose, initialProject}: {onClose: () => void; initialProject?: string}) {
    const {projects} = useSession()
    const {clone} = useWorkspace()
    const [projectId, setProjectId] = useState(initialProject ?? projects[0]?.id ?? '')
    const [branch, setBranch] = useState('main')
    const [folder, setFolder] = useState('')
    const [error, setError] = useState<string>()
    const [busy, setBusy] = useState(false)

    const project = projects.find(p => p.id === projectId)
    const branches = useHostQuery(
        signal => projectId ? call(host.GET('/api/projects/{projectId}/branches', {params: {path: {projectId}}, signal})) : Promise.resolve([]),
        [projectId],
    )

    const submit = async (event: FormEvent) => {
        event.preventDefault()
        if (!project || !folder.trim()) return
        setBusy(true)
        setError(undefined)
        try {
            await clone(project.id, folder.trim(), branch || null)
            onClose()
        } catch (e) {
            setError(e instanceof HostError ? e.message : String(e))
        } finally {
            setBusy(false)
        }
    }

    const target = project && folder.trim() ? `${folder.trim().replace(/[\\/]+$/, '')}${folder.includes('/') ? '/' : '\\'}${project.name}` : undefined
    return (
        <Modal title="Clone a project" onClose={onClose} footer={
            <>
                <button type="button" className="button" onClick={onClose}>Cancel</button>
                <button type="submit" form="clone-form" className="button primary" disabled={!project || !folder.trim() || busy}>
                    <Icon name="download" size={14}/>
                    Clone
                </button>
            </>
        }>
            <form id="clone-form" className="form" onSubmit={submit}>
                {error && <Banner tone="error">{error}</Banner>}
                {projects.length === 0 ? (
                    <Banner tone="info">There are no projects you can see on this server yet. Create one with “New project from a folder”.</Banner>
                ) : (
                    <label className="field">
                        <span>Project</span>
                        <select value={projectId} onChange={e => {
                            setProjectId(e.target.value)
                            setBranch('main')
                        }}>
                            {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                        </select>
                    </label>
                )}
                <label className="field">
                    <span>Branch</span>
                    <select value={branch} onChange={e => setBranch(e.target.value)}>
                        {(branches.data ?? [{id: 'main', name: 'main'}]).map(b => <option key={b.id} value={b.name}>{b.name}</option>)}
                    </select>
                </label>
                <FolderField label="Clone into" value={folder} onChange={setFolder} title="Choose where to put the workspace"
                             hint={target ? `The files go in ${target}` : 'A folder named after the project is created here.'}/>
            </form>
        </Modal>
    )
}

/** New project: a folder (with or without files) becomes the workspace of a new project on the server. */
export function NewProjectDialog({onClose}: {onClose: () => void}) {
    const {init} = useWorkspace()
    const [folder, setFolder] = useState('')
    const [name, setName] = useState('')
    const [template, setTemplate] = useState('auto')
    const [error, setError] = useState<string>()
    const [busy, setBusy] = useState(false)
    const templates = useHostQuery(signal => call(host.GET('/api/workspaces/templates', {signal})), [])

    const folderName = folder.trim().replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? ''
    const submit = async (event: FormEvent) => {
        event.preventDefault()
        if (!folder.trim()) return
        setBusy(true)
        setError(undefined)
        try {
            await init(folder.trim(), name.trim() || null, template === 'auto' ? null : template)
            onClose()
        } catch (e) {
            setError(e instanceof HostError ? e.message : String(e))
        } finally {
            setBusy(false)
        }
    }

    return (
        <Modal title="New project" onClose={onClose} footer={
            <>
                <button type="button" className="button" onClick={onClose}>Cancel</button>
                <button type="submit" form="new-form" className="button primary" disabled={!folder.trim() || busy}>
                    <Icon name="plus" size={14}/>
                    Create project
                </button>
            </>
        }>
            <form id="new-form" className="form" onSubmit={submit}>
                {error && <Banner tone="error">{error}</Banner>}
                <FolderField label="Folder" value={folder} onChange={setFolder} title="Choose the project folder"
                             hint="Files already in it stay, and show up as changes to submit."/>
                <label className="field">
                    <span>Project name</span>
                    <input value={name} onChange={e => setName(e.target.value)} placeholder={folderName || 'MyGame'}/>
                </label>
                <label className="field">
                    <span>Ignore rules</span>
                    <select value={template} onChange={e => setTemplate(e.target.value)}>
                        <option value="auto">Detect from the folder</option>
                        {(templates.data ?? []).map(t => <option key={t.name} value={t.name}>{t.displayName}</option>)}
                        <option value="none">None</option>
                    </select>
                    <small>Writes a starter .vxignore so build output and caches aren't submitted.</small>
                </label>
            </form>
        </Modal>
    )
}
