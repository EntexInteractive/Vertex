import type {ReactNode} from 'react'

// Line icons drawn on a 24x24 grid with a 1.65 stroke, matching the design.

export type IconName =
    | 'archive'
    | 'branch'
    | 'bug'
    | 'check'
    | 'chevron'
    | 'clock'
    | 'cloud'
    | 'code'
    | 'commit'
    | 'file'
    | 'folder'
    | 'graph'
    | 'history'
    | 'inbox'
    | 'lock'
    | 'logout'
    | 'message'
    | 'more'
    | 'plus'
    | 'refresh'
    | 'search'
    | 'settings'
    | 'storage'
    | 'tasks'
    | 'user'

const iconPaths: Record<IconName, ReactNode> = {
    archive: (
        <>
            <path d="M4 7h16v13H4z" />
            <path d="M3 3h18v4H3zM9 11h6" />
        </>
    ),
    branch: (
        <>
            <circle cx="6" cy="5" r="2" />
            <circle cx="18" cy="6" r="2" />
            <circle cx="6" cy="19" r="2" />
            <path d="M6 7v10M8 12h4a6 6 0 0 0 6-6" />
        </>
    ),
    bug: (
        <>
            <path d="m8 2 1.5 2h5L16 2M9 8h6a4 4 0 0 1 4 4v2a7 7 0 0 1-14 0v-2a4 4 0 0 1 4-4Z" />
            <path d="M3 13h4M17 13h4M5 8 3 6M19 8l2-2M5 18l-2 2M19 18l2 2M12 8v11" />
        </>
    ),
    check: <path d="m5 12 4 4L19 6" />,
    chevron: <path d="m9 18 6-6-6-6" />,
    clock: (
        <>
            <circle cx="12" cy="12" r="9" />
            <path d="M12 7v5l3 2" />
        </>
    ),
    cloud: <path d="M7 18h10a5 5 0 0 0 .4-10A7 7 0 0 0 4.2 9.5 4.5 4.5 0 0 0 7 18Z" />,
    code: (
        <>
            <path d="m8 9-3 3 3 3M16 9l3 3-3 3M14 5l-4 14" />
        </>
    ),
    commit: (
        <>
            <circle cx="12" cy="12" r="3" />
            <path d="M3 12h6M15 12h6" />
        </>
    ),
    file: (
        <>
            <path d="M6 2h8l4 4v16H6z" />
            <path d="M14 2v5h5" />
        </>
    ),
    folder: <path d="M3 6h7l2 2h9v11H3z" />,
    graph: (
        <>
            <circle cx="5" cy="5" r="2" />
            <circle cx="19" cy="7" r="2" />
            <circle cx="6" cy="19" r="2" />
            <path d="M5 7v10M7 12h4a8 8 0 0 0 8-5" />
        </>
    ),
    history: (
        <>
            <path d="M3 12a9 9 0 1 0 3-6.7L3 8" />
            <path d="M3 3v5h5M12 7v5l3 2" />
        </>
    ),
    inbox: (
        <>
            <path d="M4 4h16v16H4z" />
            <path d="M4 14h5l2 3h2l2-3h5" />
        </>
    ),
    lock: (
        <>
            <rect x="5" y="11" width="14" height="10" rx="2" />
            <path d="M8 11V7a4 4 0 0 1 8 0v4" />
        </>
    ),
    logout: (
        <>
            <path d="M15 4h4v16h-4" />
            <path d="M10 8l-4 4 4 4M6 12h10" />
        </>
    ),
    message: <path d="M4 4h16v13H8l-4 4z" />,
    more: (
        <>
            <circle cx="5" cy="12" r="1" />
            <circle cx="12" cy="12" r="1" />
            <circle cx="19" cy="12" r="1" />
        </>
    ),
    plus: <path d="M12 5v14M5 12h14" />,
    refresh: (
        <>
            <path d="M20 6v5h-5M4 18v-5h5" />
            <path d="M18.5 9A7 7 0 0 0 6 6.5L4 9M5.5 15A7 7 0 0 0 18 17.5l2-2.5" />
        </>
    ),
    search: (
        <>
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-4-4" />
        </>
    ),
    settings: (
        <>
            <circle cx="12" cy="12" r="3" />
            <path d="M19 15.5l2-1v-5l-2-1-.7-1.7.7-2-3.5-2-1.5 1.4-2-.3L10.5 2h-4L6 4l-2 1-2-.6-2 3.5 1.5 1.5-.3 2L0 13v4l2 .5 1 2-.6 2 3.5 2 1.5-1.5 2 .3L11 24h4l.5-2 2-1 2 .6 2-3.5-1.5-1.5Z" />
        </>
    ),
    storage: (
        <>
            <ellipse cx="12" cy="5" rx="8" ry="3" />
            <path d="M4 5v7c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12v7c0 1.7 3.6 3 8 3s8-1.3 8-3v-7" />
        </>
    ),
    tasks: (
        <>
            <path d="M9 5h12M9 12h12M9 19h12" />
            <path d="m3 5 1 1 2-2M3 12l1 1 2-2M3 19l1 1 2-2" />
        </>
    ),
    user: (
        <>
            <circle cx="12" cy="8" r="4" />
            <path d="M4 21a8 8 0 0 1 16 0" />
        </>
    ),
}

export function Icon({ name, size = 17 }: { name: IconName; size?: number }) {
    return (
        <svg
            aria-hidden="true"
            width={size}
            height={size}
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.65"
            strokeLinecap="round"
            strokeLinejoin="round"
        >
            {iconPaths[name]}
        </svg>
    )
}
