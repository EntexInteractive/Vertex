import {type ReactNode, useEffect, useMemo, useState} from 'react'
import {call, hasDesktopHost, host, HostError} from '../host/client'
import {type Branch, type Project, type Session, SessionContext, type SessionState} from './session'

const PROJECT_KEY = 'vertex.project'
const BRANCH_KEY = 'vertex.branch'

function remembered(key: string): string | null {
    try {
        return localStorage.getItem(key)
    } catch {
        return null
    }
}

function remember(key: string, value: string) {
    try {
        localStorage.setItem(key, value)
    } catch {
        // Not remembering the selection only costs a click next time.
    }
}

function toHostError(error: unknown): HostError {
    return error instanceof HostError ? error : new HostError(String(error), 0)
}

/**
 * Loads the session, then the projects once logged in, then the selected project's branches. The selection is
 * remembered per machine, and falls back to the first project and the `main` branch.
 */
export function SessionProvider({children}: {children: ReactNode}) {
    const [session, setSession] = useState<Session>()
    const [projects, setProjects] = useState<Project[]>([])
    const [branches, setBranches] = useState<Branch[]>([])
    const [projectId, setProjectId] = useState(() => remembered(PROJECT_KEY))
    const [branchId, setBranchId] = useState(() => remembered(BRANCH_KEY))
    const [error, setError] = useState<HostError>()

    useEffect(() => {
        if (!hasDesktopHost()) return
        const controller = new AbortController()
        call(host.GET('/api/session', {signal: controller.signal})).then(setSession, (e: unknown) => {
            if (!controller.signal.aborted) setError(toHostError(e))
        })
        return () => controller.abort()
    }, [])

    const loggedIn = session?.loggedIn === true
    const server = session?.server
    useEffect(() => {
        if (!loggedIn) return
        const controller = new AbortController()
        call(host.GET('/api/projects', {signal: controller.signal})).then(
            list => {
                setProjects(list)
                setError(undefined)
            },
            (e: unknown) => {
                if (!controller.signal.aborted) setError(toHostError(e))
            },
        )
        return () => controller.abort()
    }, [loggedIn, server])

    const project = projects.find(p => p.id === projectId) ?? projects[0]
    useEffect(() => {
        if (!loggedIn || !project) return
        const controller = new AbortController()
        call(host.GET('/api/projects/{projectId}/branches', {params: {path: {projectId: project.id}}, signal: controller.signal})).then(
            setBranches,
            (e: unknown) => {
                if (!controller.signal.aborted) setError(toHostError(e))
            },
        )
        return () => controller.abort()
    }, [loggedIn, project])

    const branch = branches.find(b => b.id === branchId) ?? branches.find(b => b.name === 'main') ?? branches[0]

    const state = useMemo<SessionState>(() => ({
        session,
        error,
        setSession: next => {
            setSession(next)
            setError(undefined)
            if (!next.loggedIn) {
                setProjects([])
                setBranches([])
            }
        },
        projects: loggedIn ? projects : [],
        project: loggedIn ? project : undefined,
        selectProject: id => {
            setProjectId(id)
            remember(PROJECT_KEY, id)
            setBranches([])
        },
        branches: loggedIn ? branches : [],
        branch: loggedIn ? branch : undefined,
        selectBranch: id => {
            setBranchId(id)
            remember(BRANCH_KEY, id)
        },
    }), [session, loggedIn, error, projects, project, branches, branch])

    return <SessionContext.Provider value={state}>{children}</SessionContext.Provider>
}
