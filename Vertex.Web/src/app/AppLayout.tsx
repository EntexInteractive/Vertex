import {Navigate, Outlet} from 'react-router'
import {Rail} from './layout/Rail'
import {Toolbar} from './layout/Toolbar'
import {UtilityDock} from './layout/UtilityDock'
import {useSession} from './session'

/** The frame around every page except login: rail, toolbar, page, utility dock. */
export function AppLayout() {
    const {session} = useSession()
    if (session?.loggedIn === false) return <Navigate to="/account" replace/>

    return (
        <div className="desktop-app">
            <div className="app-body">
                <Rail/>
                <div className="app-main">
                    <Toolbar/>
                    <Outlet/>
                </div>
            </div>
            <UtilityDock/>
        </div>
    )
}
