import {createContext, useContext} from 'react'
import type {HostError, HostSchemas} from '../host/client'

export type Session = HostSchemas['SessionDto']
export type Project = HostSchemas['ProjectDto']
export type Branch = HostSchemas['BranchDto']

export type SessionState = {
    /** Who is logged in where. Undefined until the first answer, and always outside the desktop app. */
    session: Session | undefined
    /** Why the session, projects or branches couldn't be loaded, if they couldn't. */
    error: HostError | undefined
    setSession: (session: Session) => void

    projects: Project[]
    project: Project | undefined
    selectProject: (id: string) => void

    branches: Branch[]
    branch: Branch | undefined
    selectBranch: (id: string) => void
}

export const SessionContext = createContext<SessionState | null>(null)

/** The login and the selected project and branch, shared by the toolbar and every page. */
export function useSession(): SessionState {
    const state = useContext(SessionContext)
    if (!state) throw new Error('useSession must be used inside SessionProvider.')
    return state
}
