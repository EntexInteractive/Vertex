import {useCallback, useEffect, useMemo, useRef, useState} from 'react'
import {Navigate, Outlet, useLocation} from 'react-router'
import {CloneDialog, NewProjectDialog} from '../features/welcome/WorkspaceDialogs'
import {WelcomePage} from '../features/welcome/WelcomePage'
import {hasDesktopHost} from '../host/client'
import {CommandPalette} from './layout/CommandPalette'
import {ContextBar} from './layout/ContextBar'
import {Rail} from './layout/Rail'
import {ResultToast} from './layout/ResultToast'
import {StatusBar} from './layout/StatusBar'
import {useSession} from './session'
import {ShellContext, type ShellDialog, type ShellState} from './shell'
import {useWorkspace} from './workspace'

/**
 * The frame around every page except login: the context bar (workspace, branch, main action), the rail, the page,
 * and the status bar. With no workspace open, the page area shows the welcome screen.
 */
export function AppLayout() {
    const {session} = useSession()
    const {ready, current} = useWorkspace()
    const location = useLocation()
    const [palette, setPalette] = useState(false)
    const [dialog, setDialog] = useState<ShellDialog | null>(null)
    const submitFocus = useRef<(() => void) | null>(null)

    useEffect(() => {
        const onKey = (event: KeyboardEvent) => {
            if ((event.ctrlKey || event.metaKey) && (event.key === 'k' || event.key === 'K' || (event.shiftKey && event.key === 'P'))) {
                event.preventDefault()
                setPalette(open => !open)
            }
        }
        document.addEventListener('keydown', onKey)
        return () => document.removeEventListener('keydown', onKey)
    }, [])

    const closePalette = useCallback(() => setPalette(false), [])
    const closeDialog = useCallback(() => setDialog(null), [])
    const shell = useMemo<ShellState>(() => ({
        openPalette: () => setPalette(true),
        openDialog: setDialog,
        focusSubmit: () => submitFocus.current?.(),
        registerSubmitFocus: focus => {
            submitFocus.current = focus
        },
    }), [])

    if (session?.loggedIn === false) return <Navigate to="/account" replace/>

    const showWelcome = hasDesktopHost() && ready && !current && !location.pathname.startsWith('/history')
    return (
        <ShellContext.Provider value={shell}>
            <div className="desktop-app">
                <ContextBar/>
                <div className="app-body">
                    <Rail/>
                    <main className="app-main">{showWelcome ? <WelcomePage/> : ready ? <Outlet/> : null}</main>
                </div>
                <StatusBar/>
                <ResultToast/>
                {palette && <CommandPalette onClose={closePalette}/>}
                {dialog === 'clone' && <CloneDialog onClose={closeDialog}/>}
                {dialog === 'new' && <NewProjectDialog onClose={closeDialog}/>}
            </div>
        </ShellContext.Provider>
    )
}
