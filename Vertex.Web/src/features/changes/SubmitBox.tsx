import {useEffect, useRef, useState} from 'react'
import {useShell} from '../../app/shell'
import {useWorkspace} from '../../app/workspace'
import {Icon} from '../../components/Icon'
import {Kbd} from '../../components/ui'
import {plural} from '../../lib/format'

function draftKey(root: string) {
    return `vertex.submitDraft.${root}`
}

function readDraft(root: string): string {
    try {
        return localStorage.getItem(draftKey(root)) ?? ''
    } catch {
        return ''
    }
}

/**
 * The message box and Submit button under the changes. The message is kept as a draft per workspace until it's
 * submitted, so switching pages or restarting doesn't lose it.
 */
export function SubmitBox({workspaceRoot, paths, total}: {workspaceRoot: string; paths: string[]; total: number}) {
    const {submit, operation, status, result} = useWorkspace()
    const {registerSubmitFocus} = useShell()
    const [message, setMessage] = useState(() => readDraft(workspaceRoot))
    const input = useRef<HTMLTextAreaElement>(null)

    useEffect(() => {
        registerSubmitFocus(() => input.current?.focus())
        return () => registerSubmitFocus(null)
    }, [registerSubmitFocus])

    useEffect(() => {
        try {
            if (message) localStorage.setItem(draftKey(workspaceRoot), message)
            else localStorage.removeItem(draftKey(workspaceRoot))
        } catch {
            // Only the draft is lost.
        }
    }, [message, workspaceRoot])

    // Keep the message until the submit lands; a refused one (a lock, nothing left to submit) keeps the draft.
    const [submitted, setSubmitted] = useState<string>()
    if (submitted !== undefined && result?.kind === 'submit') {
        setSubmitted(undefined)
        if (result.result.ok && message === submitted) setMessage('')
    }

    const busy = operation !== undefined
    const offline = status?.serverProblem ? "The server didn't answer the last scan." : undefined
    const ready = message.trim().length > 0 && paths.length > 0 && !busy
    const run = async () => {
        if (!ready) return
        const text = message
        if (await submit(text.trim(), paths)) setSubmitted(text)
    }

    const label = paths.length === 0 ? 'Submit' : paths.length === total && total > 1 ? `Submit all ${paths.length.toLocaleString()}` : `Submit ${plural(paths.length, 'file')}`
    return (
        <div className="submit-box">
            <textarea
                ref={input}
                value={message}
                onChange={event => setMessage(event.target.value)}
                onKeyDown={event => {
                    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                        event.preventDefault()
                        void run()
                    }
                }}
                placeholder="Describe what changed"
                rows={3}
                aria-label="What changed"
            />
            <div className="submit-row">
                <small className="muted">
                    {offline ?? (paths.length === 0 && total > 0 ? 'Check the files to submit.' : <><Kbd>Ctrl</Kbd> + <Kbd>Enter</Kbd></>)}
                </small>
                <button type="button" className="button primary" disabled={!ready} onClick={() => void run()}>
                    <Icon name="upload" size={14}/>
                    {label}
                </button>
            </div>
        </div>
    )
}
