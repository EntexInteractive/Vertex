import {useEffect} from 'react'
import {Icon} from '../../components/Icon'
import {useWorkspace} from '../workspace'

const SUCCESS_MS = 6_000

/** How the last operation ended, in the bottom right. Successes fade on their own; problems stay until dismissed. */
export function ResultToast() {
    const {result, dismissResult} = useWorkspace()

    const quiet = result?.result.ok === true && result.result.warnings.length === 0 && result.result.conflicts.length === 0
    useEffect(() => {
        if (!quiet) return
        const timer = setTimeout(dismissResult, SUCCESS_MS)
        return () => clearTimeout(timer)
    }, [quiet, result, dismissResult])

    if (!result) return null
    const {ok, summary, warnings, conflicts} = result.result
    return (
        <div className={`result-toast ${ok ? (warnings.length || conflicts.length ? 'warning' : 'success') : 'error'}`} role={ok ? 'status' : 'alert'}>
            <Icon name={ok ? (warnings.length || conflicts.length ? 'alert' : 'check') : 'alert'} size={16}/>
            <div>
                <b>{summary}</b>
                {warnings.map(warning => <p key={warning}>{warning}</p>)}
                {conflicts.length > 0 && (
                    <p className="muted">They're marked on the Changes page.</p>
                )}
            </div>
            <button type="button" className="icon-button" aria-label="Dismiss" onClick={dismissResult}>
                <Icon name="close" size={14}/>
            </button>
        </div>
    )
}
